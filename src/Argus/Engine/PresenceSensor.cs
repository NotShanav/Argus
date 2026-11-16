using System;
using Windows.Devices.Sensors;

namespace PresenceGuard
{
    public sealed class PresenceSensor : IPresenceSensor
    {
        private HumanPresenceSensor? _sensor;
        private PresenceReading _current = new(PresenceState.Unknown, null);
        private readonly object _lock = new();
        public bool Available { get; }
        public PresenceReading Current { get { lock (_lock) return _current; } }
        public event Action<PresenceReading>? Changed;

        public PresenceSensor()
        {
            try
            {
                _sensor = HumanPresenceSensor.GetDefault();
                if (_sensor == null) return;
                Refresh(false);
                _sensor.ReadingChanged += (s, e) => Refresh(true);
                Available = true;
            }
            catch { _sensor = null; Available = false; _current = new PresenceReading(PresenceState.Unknown, null); }
        }

        private static PresenceState Map(string? s) => s switch
        {
            "Present" => PresenceState.Present,
            "NotPresent" => PresenceState.Absent,
            _ => PresenceState.Unknown
        };

        private void Refresh(bool raise)
        {
            try
            {
                var r = _sensor?.GetCurrentReading();
                if (r == null) return;
                var state = Map(r.Presence.ToString());
                bool? onlooker = null;
                try
                {
                    // OnlookerPresence ships in SDK 26100+; read it by reflection so we still compile against 22621.
                    var p = r.GetType().GetProperty("OnlookerPresence");
                    var v = p?.GetValue(r)?.ToString();
                    if (v == "Present") onlooker = true;
                    else if (v == "NotPresent") onlooker = false;
                }
                catch { }
                var reading = new PresenceReading(state, onlooker);
                lock (_lock) _current = reading;
                if (raise) Changed?.Invoke(reading);
            }
            catch { }
        }

        public void Dispose() { _sensor = null; }
    }
}
