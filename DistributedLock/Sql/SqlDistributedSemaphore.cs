﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Threading.Sql
{
    public class SqlDistributedSemaphore : IDistributedLock
    {
        private readonly IInternalSqlDistributedLock internalLock;
        private readonly SqlSemaphore strategy;

        #region ---- Constructors ----
        /// <summary>
        /// Creates a semaphore with name <paramref name="semaphoreName"/> that can be acquired up to <paramref name="maxCount"/> 
        /// times concurrently. Uses the given <paramref name="connectionString"/> to connect to the database.
        /// 
        /// Uses <see cref="SqlDistributedLockConnectionStrategy.Default"/>
        /// </summary>
        public SqlDistributedSemaphore(string semaphoreName, int maxCount, string connectionString)
            : this(semaphoreName, maxCount, connectionString, SqlDistributedLockConnectionStrategy.Default)
        {
        }

        /// <summary>
        /// Creates a semaphore with name <paramref name="semaphoreName"/> that can be acquired up to <paramref name="maxCount"/> 
        /// times concurrently. Uses the given <paramref name="connectionString"/> to connect to the database via the strategy
        /// specified by <paramref name="connectionStrategy"/>
        /// </summary>
        public SqlDistributedSemaphore(string semaphoreName, int maxCount, string connectionString, SqlDistributedLockConnectionStrategy connectionStrategy)
            : this(semaphoreName, maxCount, name => SqlDistributedLock.CreateInternalLock(name, connectionString, connectionStrategy))
        {
            if (string.IsNullOrEmpty(connectionString)) { throw new ArgumentNullException(nameof(connectionString)); }
        }

        /// <summary>
        /// Creates a semaphore with name <paramref name="semaphoreName"/> that can be acquired up to <paramref name="maxCount"/> 
        /// times concurrently. When acquired, the semaphore will be scoped to the given <paramref name="connection"/>. 
        /// The <paramref name="connection"/> is assumed to be externally managed: the <see cref="SqlDistributedSemaphore"/> will 
        /// not attempt to open, close, or dispose it
        /// </summary>
        public SqlDistributedSemaphore(string semaphoreName, int maxCount, IDbConnection connection)
            : this(semaphoreName, maxCount, name => new ConnectionScopedSqlDistributedLock(name, connection))
        {
            if (connection == null) { throw new ArgumentNullException(nameof(connection)); }
        }

        /// <summary>
        /// Creates a semaphore with name <paramref name="semaphoreName"/> that can be acquired up to <paramref name="maxCount"/> 
        /// times concurrently. When acquired, the semaphore will be scoped to the given <paramref name="transaction"/>. 
        /// The <paramref name="transaction"/> and its <see cref="IDbTransaction.Connection"/> are assumed to be externally managed: 
        /// the <see cref="SqlDistributedSemaphore"/> will not attempt to open, close, commit, roll back, or dispose them
        /// </summary>
        public SqlDistributedSemaphore(string semaphoreName, int maxCount, IDbTransaction transaction)
            // todo move ToSafeName call to inner method; pass through func<name, iinternal>
            : this(semaphoreName, maxCount, name => new TransactionScopedSqlDistributedLock(name, transaction))
        {
            if (transaction == null) { throw new ArgumentNullException(nameof(transaction)); }
        }

        private SqlDistributedSemaphore(string semaphoreName, int maxCount, Func<string, IInternalSqlDistributedLock> createInternalLockFromName)
        {
            if (semaphoreName == null) { throw new ArgumentNullException("lockName"); }
            if (maxCount < 1) { throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "must be positive"); }

            this.strategy = new SqlSemaphore(maxCount);
            this.internalLock = createInternalLockFromName(SqlSemaphore.ToSafeName(semaphoreName));
        }
        #endregion

        #region ---- Public API ----
        // todo all acquire doc comments
        /// <summary>
        /// Attempts to acquire the lock synchronously. Usage:
        /// <code>
        ///     using (var handle = myLock.TryAcquire(...))
        ///     {
        ///         if (handle != null) { /* we have the lock! */ }
        ///     }
        ///     // dispose releases the lock if we took it
        /// </code>
        /// </summary>
        /// <param name="timeout">How long to wait before giving up on acquiring the lock. Defaults to 0</param>
        /// <param name="cancellationToken">Specifies a token by which the wait can be canceled</param>
        /// <returns>An <see cref="IDisposable"/> "handle" which can be used to release the lock, or null if the lock was not taken</returns>
        public IDisposable TryAcquire(TimeSpan timeout = default(TimeSpan), CancellationToken cancellationToken = default(CancellationToken))
        {
            return cancellationToken.CanBeCanceled
                // use the async version since that supports cancellation
                ? DistributedLockHelpers.TryAcquireWithAsyncCancellation(this, timeout, cancellationToken)
                // synchronous mode
                : this.internalLock.TryAcquire(timeout.ToInt32Timeout(), this.strategy, contextHandle: null);
        }

        /// <summary>
        /// Acquires the lock synchronously, failing with <see cref="TimeoutException"/> if the wait times out
        /// <code>
        ///     using (myLock.Acquire(...))
        ///     {
        ///         // we have the lock
        ///     }
        ///     // dispose releases the lock
        /// </code>
        /// </summary>
        /// <param name="timeout">How long to wait before giving up on acquiring the lock. Defaults to <see cref="Timeout.InfiniteTimeSpan"/></param>
        /// <param name="cancellationToken">Specifies a token by which the wait can be canceled</param>
        /// <returns>An <see cref="IDisposable"/> "handle" which can be used to release the lock</returns>
        public IDisposable Acquire(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return DistributedLockHelpers.Acquire(this, timeout, cancellationToken);
        }

        // todo comments
        /// <summary>
        /// Attempts to acquire the lock asynchronously. Usage:
        /// <code>
        ///     using (var handle = await myLock.TryAcquireAsync(...))
        ///     {
        ///         if (handle != null) { /* we have the lock! */ }
        ///     }
        ///     // dispose releases the lock if we took it
        /// </code>
        /// </summary>
        /// <param name="timeout">How long to wait before giving up on acquiring the lock. Defaults to 0</param>
        /// <param name="cancellationToken">Specifies a token by which the wait can be canceled</param>
        /// <returns>An <see cref="IDisposable"/> "handle" which can be used to release the lock, or null if the lock was not taken</returns>
        public AwaitableDisposable<IDisposable> TryAcquireAsync(TimeSpan timeout = default(TimeSpan), CancellationToken cancellationToken = default(CancellationToken))
        {
            return new AwaitableDisposable<IDisposable>(this.internalLock.TryAcquireAsync(timeout.ToInt32Timeout(), this.strategy, cancellationToken, contextHandle: null));
        }

        /// <summary>
        /// Acquires the lock asynchronously, failing with <see cref="TimeoutException"/> if the wait times out
        /// <code>
        ///     using (await myLock.AcquireAsync(...))
        ///     {
        ///         // we have the lock
        ///     }
        ///     // dispose releases the lock
        /// </code>
        /// </summary>
        /// <param name="timeout">How long to wait before giving up on acquiring the lock. Defaults to <see cref="Timeout.InfiniteTimeSpan"/></param>
        /// <param name="cancellationToken">Specifies a token by which the wait can be canceled</param>
        /// <returns>An <see cref="IDisposable"/> "handle" which can be used to release the lock</returns>
        public AwaitableDisposable<IDisposable> AcquireAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return new AwaitableDisposable<IDisposable>(DistributedLockHelpers.AcquireAsync(this, timeout, cancellationToken));
        }
        #endregion

        #region ---- IDistributedLock Compat Layer (for Testing) ----
        Task<IDisposable> IDistributedLock.TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            return this.TryAcquireAsync(timeout, cancellationToken).Task;
        }

        Task<IDisposable> IDistributedLock.AcquireAsync(TimeSpan? timeout, CancellationToken cancellationToken)
        {
            return this.AcquireAsync(timeout, cancellationToken).Task;
        }
        #endregion
    }
}
