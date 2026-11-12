using System;

namespace LumINE.Utils
{
    // Network receive may continue while the window is hidden. Only the UI
    // thread resumes presentation, after activation and a dispatcher turn.
    public static class ForegroundUi
    {
        private static readonly object Gate = new object();
        private static int _generation;
        private static bool _ready;
        public static event Action Ready;
        public static int Generation { get { lock (Gate) return _generation; } }
        public static bool IsReady { get { lock (Gate) return _ready; } }
        public static bool IsCurrent(int generation) { lock (Gate) return _ready && _generation == generation; }
        public static void Pause()
        {
            lock (Gate) { _ready = false; _generation++; }
        }
        public static bool Resume(int generation)
        {
            lock (Gate)
            {
                if (_ready || _generation != generation) return false;
                _ready = true;
            }
            var handler = Ready;
            if (handler != null) handler();
            return true;
        }
    }
}
