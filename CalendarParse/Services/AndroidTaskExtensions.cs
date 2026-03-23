#if ANDROID
using Android.Gms.Tasks;

namespace CalendarParse.Services
{
    /// <summary>
    /// Bridges Android's Java <see cref="Android.Gms.Tasks.Task"/> to a .NET <see cref="Task{T}"/>.
    /// </summary>
    internal static class AndroidTaskExtensions
    {
        public static Task<TResult> AsTaskAsync<TResult>(
            this Android.Gms.Tasks.Task androidTask,
            System.Threading.CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            androidTask
                .AddOnSuccessListener(new OnSuccessListener<TResult>(result => tcs.TrySetResult(result)))
                .AddOnFailureListener(new OnFailureListener(ex => tcs.TrySetException(new Exception(ex.LocalizedMessage ?? ex.ToString()))));

            ct.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

            return tcs.Task;
        }

        private sealed class OnSuccessListener<T>(Action<T> callback) : Java.Lang.Object, IOnSuccessListener
        {
            public void OnSuccess(Java.Lang.Object? result) => callback((T)(object)result!);
        }

        private sealed class OnFailureListener(Action<Java.Lang.Exception> callback) : Java.Lang.Object, IOnFailureListener
        {
            public void OnFailure(Java.Lang.Exception e) => callback(e);
        }
    }
}
#endif
