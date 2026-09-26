namespace WardogsRadio.Core;
public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public sealed record TimelineCursor(int SequenceIndex, double PositionSeconds, bool Ended, bool NeedsDuration);
public sealed class VirtualPlaybackTimeline(IClock clock)
{
    public TimelineCursor Resolve(Station station, IReadOnlyDictionary<string, YouTubeTrackMetadata> durations)
    {
        var runtime=station.Runtime; var position=runtime.PositionSeconds; if(!runtime.VirtualRunning || runtime.VirtualStartUtc is null) return new(runtime.SequenceIndex,position,false,false);
        var elapsed=Math.Max(0,(clock.UtcNow-runtime.VirtualStartUtc.Value).TotalSeconds)*Math.Max(.1,runtime.PlaybackRate); var index=runtime.SequenceIndex;
        if(runtime.Sequence.Count==0) return new(index,position,false,true);
        while(elapsed>0){ var id=runtime.Sequence[index]; if(!durations.TryGetValue(id,out var info) || info.DurationSeconds<=0) return new(index,position,false,true); var remaining=Math.Max(0,info.DurationSeconds-position); if(elapsed<remaining) return new(index,position+elapsed,false,false); elapsed-=remaining; position=0; index++; if(index>=runtime.Sequence.Count){ if(!station.Loop) return new(runtime.Sequence.Count-1,info.DurationSeconds,true,false); index=0; } }
        return new(index,position,false,false);
    }
    public void Pause(Station s, IReadOnlyDictionary<string,YouTubeTrackMetadata> durations){var c=Resolve(s,durations); s.Runtime.SequenceIndex=c.SequenceIndex;s.Runtime.PositionSeconds=c.PositionSeconds;s.Runtime.VirtualRunning=false;s.Runtime.VirtualStartUtc=null;s.Runtime.WasPlaying=false;}
    public void Resume(Station s){s.Runtime.WasPlaying=true;s.Runtime.VirtualRunning=true;s.Runtime.VirtualStartUtc=clock.UtcNow;}
    public void Navigate(Station s, int delta){if(s.Runtime.Sequence.Count==0)return; s.Runtime.SequenceIndex=(s.Runtime.SequenceIndex+delta+s.Runtime.Sequence.Count)%s.Runtime.Sequence.Count;s.Runtime.PositionSeconds=0;if(s.Runtime.VirtualRunning)s.Runtime.VirtualStartUtc=clock.UtcNow;}
    public void Seek(Station s,double seconds){s.Runtime.PositionSeconds=Math.Max(0,seconds);if(s.Runtime.VirtualRunning)s.Runtime.VirtualStartUtc=clock.UtcNow;}
}
public static class TransitionMath { public static (double Outgoing,double Incoming) Gains(double progress, TransitionCurve curve) { var t=Math.Clamp(progress,0,1); return curve switch { TransitionCurve.EqualPower => (Math.Cos(t*Math.PI/2),Math.Sin(t*Math.PI/2)), TransitionCurve.Smoothstep => (1-(t*t*(3-2*t)),t*t*(3-2*t)), _ => (1-t,t)}; } }
