using System;

namespace DemoRipper2026
{
    /// <summary>
    /// Anything that can record the game. Callers drive one of these and
    /// shouldn't care what is behind it: OBS, or the built-in recorder.
    ///
    /// Every call blocks until the recorder confirms, so call from a worker
    /// thread, never the UI thread.
    /// </summary>
    public interface IRecorder : IDisposable
    {
        string Name { get; }

        bool IsRecording { get; }

        /// <summary>
        /// Something went wrong that didn't stop the recording, or null.
        /// Reset by every Start.
        /// </summary>
        string LastWarning { get; }

        /// <summary>
        /// The file of the last recording that ended, including one that ended
        /// without Stop (OBS's own button, the game closing). Null when unknown.
        /// </summary>
        string LastStoppedPath { get; }

        /// <summary>
        /// Throws if the recorder can't start right now: unreachable, or already
        /// recording something it didn't start. Call before committing to a take,
        /// so a problem shows up when the user presses Record, not at the start tick.
        /// </summary>
        void Preflight();

        /// <summary>
        /// Gets ready to record to this path so Start is as quick as the
        /// recorder allows. Optional: Start prepares if this wasn't called.
        /// </summary>
        void Prepare(string pathWithoutExtension);

        /// <summary>Releases a prepared take that won't be started. Does nothing while recording.</summary>
        void Cancel();

        /// <summary>
        /// Starts recording to a folder and base name. The container, and so
        /// the extension, is the recorder's choice (OBS uses the profile's
        /// format), which is why the path has no extension.
        /// </summary>
        void Start(string pathWithoutExtension);

        /// <summary>
        /// Stops and returns the full path of the finished file, closed and
        /// safe to move. Null when the recorder couldn't tell.
        /// </summary>
        string Stop();
    }
}
