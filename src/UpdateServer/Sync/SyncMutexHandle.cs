using System;
using System.Threading;

namespace UpdateServer.Sync
{
    internal sealed class SyncMutexHandle : IDisposable
    {
        private readonly Mutex mutex;
        private bool disposed;

        private SyncMutexHandle(Mutex mutexInstance)
        {
            mutex = mutexInstance;
        }

        public static SyncMutexHandle Acquire(string targetHash)
        {
            bool createdNew = false;
            Mutex mutex = new Mutex(false, @"Local\PugGet5Sync_" + targetHash, out createdNew);
            bool acquired = false;

            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                throw new InvalidOperationException("Another Pug/Get5 sync is already running for this folder.");
            }

            return new SyncMutexHandle(mutex);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
            }

            mutex.Dispose();
        }
    }
}
