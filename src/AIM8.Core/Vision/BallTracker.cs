using AIM8.Core.Geometry;

namespace AIM8.Core.Vision;

/// <summary>
/// Follows balls from frame to frame. It averages the position of a ball that
/// stays put, takes a majority vote on what the ball is (a rolling stripe can
/// briefly show only its band), and reports how long the table has been still -
/// a shot is only planned once nothing is moving.
/// </summary>
public sealed class BallTracker
{
    /// <summary>A ball whose centre shifts more than this share of a radius is moving.</summary>
    private const double MoveTolerance = 0.3;

    private const int MaxSamples = 10;

    private const int MissedFramesAllowed = 3;

    private readonly List<Track> _tracks = [];
    private int _nextId = 1;

    /// <summary>Consecutive updates in which no ball moved, appeared or vanished.</summary>
    public int StillFrames { get; private set; }

    public void Reset()
    {
        _tracks.Clear();
        StillFrames = 0;
    }

    /// <param name="covered">
    /// Whether something still sits where a ball was last seen. A ball hidden
    /// under the game's aim ring or the cue is kept; one that left bare cloth
    /// behind was potted.
    /// </param>
    public IReadOnlyList<Ball> Update(IReadOnlyList<ClassifiedBall> detections, double radius, Func<Vec2, bool>? covered = null)
    {
        var changed = false;
        var trackTaken = new bool[_tracks.Count];
        var matched = new int[detections.Count];
        Array.Fill(matched, -1);

        var pairs = new List<(double Distance, int Track, int Detection)>();
        for (var t = 0; t < _tracks.Count; t++)
        {
            for (var d = 0; d < detections.Count; d++)
            {
                var distance = Vec2.Distance(_tracks[t].Latest, detections[d].Position);
                if (distance < 0.9 * radius) pairs.Add((distance, t, d));
            }
        }

        foreach (var (_, t, d) in pairs.OrderBy(p => p.Distance))
        {
            if (trackTaken[t] || matched[d] >= 0) continue;
            trackTaken[t] = true;
            matched[d] = t;
        }

        for (var d = 0; d < detections.Count; d++)
        {
            if (matched[d] >= 0)
            {
                if (_tracks[matched[d]].Observe(detections[d], radius)) changed = true;
                continue;
            }

            var track = new Track(_nextId++);
            track.Observe(detections[d], radius);
            _tracks.Add(track);
            changed = true;
        }

        for (var t = trackTaken.Length - 1; t >= 0; t--)
        {
            if (trackTaken[t]) continue;

            // The cue stick sweeps over balls while you aim, so a ball that
            // drops out for a frame or two is kept where it was; only a ball
            // gone for longer (potted) counts as a change.
            var track = _tracks[t];
            if (covered is not null && covered(track.Position)) continue;

            track.Missed++;
            if (track.Missed < MissedFramesAllowed) continue;

            _tracks.RemoveAt(t);
            changed = true;
        }

        StillFrames = changed ? 0 : StillFrames + 1;

        return _tracks.Select(t => t.ToBall()).ToList();
    }

    private sealed class Track(int id)
    {
        private readonly Dictionary<(BallKind Kind, int Number), double> _votes = [];
        private Vec2 _sum;
        private int _samples;
        private string _color = "#ffffff";
        private double _fit;

        public int Id { get; } = id;

        public int Missed { get; set; }

        public Vec2 Latest { get; private set; }

        public Vec2 Position => _samples == 0 ? Latest : _sum / _samples;

        /// <summary>Returns true when the ball moved since the last frame.</summary>
        public bool Observe(ClassifiedBall detection, double radius)
        {
            Missed = 0;
            var moved = _samples == 0 || Vec2.Distance(Position, detection.Position) > MoveTolerance * radius;
            Latest = detection.Position;

            if (moved)
            {
                _sum = detection.Position;
                _samples = 1;
                foreach (var previous in _votes.Keys.ToList()) _votes[previous] *= 0.5;
            }
            else
            {
                if (_samples >= MaxSamples)
                {
                    _sum = _sum * ((MaxSamples - 1) / (double)_samples);
                    _samples = MaxSamples - 1;
                }

                _sum += detection.Position;
                _samples++;
            }

            // A clean outline is a clean view; a ball half under the stick votes less.
            var key = (detection.Kind, detection.Number);
            var visible = detection.Look.Visible;
            _votes[key] = _votes.GetValueOrDefault(key) + (0.2 + detection.Fit) * visible * visible;
            _color = detection.Look.Hex;
            _fit = detection.Fit;
            return moved;
        }

        public Ball ToBall()
        {
            var total = _votes.Values.Sum();
            var best = _votes.MaxBy(v => v.Value);
            var kindVotes = _votes.Where(v => v.Key.Kind == best.Key.Kind).Sum(v => v.Value);
            var confidence = total > 0 ? kindVotes / total * _fit : 0;
            return new Ball(Id, Position, best.Key.Kind, best.Key.Number, _color, confidence);
        }
    }
}
