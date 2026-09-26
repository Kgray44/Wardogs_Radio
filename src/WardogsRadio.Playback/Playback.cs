using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using WardogsRadio.Core;

namespace WardogsRadio.Playback;
public sealed record PlaybackCapabilities(bool VisiblePlaybackRequired,bool BackgroundPlayback,bool SimultaneousInstances,bool Seek,bool PlaylistNavigation,bool VolumeControl,bool Metadata,bool VirtualRadioTimeline, bool TrueRadioPlayback);
public sealed record SourceValidationResult(bool IsValid,string Message,string? CanonicalSource=null);
public sealed record TrackMetadata(string Title,string? Artist=null,string? ArtworkUrl=null);
public sealed record PlaybackSnapshot(bool IsPlaying,double PositionSeconds,double? DurationSeconds,TrackMetadata? Track,ProviderHealth Health);
public sealed record MpvAudioDevice(string Name, string Description);
public interface IPlaybackProvider : IAsyncDisposable
{
 string ProviderId {get;} PlaybackCapabilities Capabilities {get;} PlaybackSnapshot Snapshot {get;} event EventHandler? StateChanged;
 Task<SourceValidationResult> ValidateSourceAsync(string source,CancellationToken ct=default); Task LoadAsync(Station station,CancellationToken ct=default); Task PlayAsync(CancellationToken ct=default); Task PauseAsync(CancellationToken ct=default); Task StopAsync(CancellationToken ct=default); Task NextAsync(CancellationToken ct=default); Task PreviousAsync(CancellationToken ct=default); Task SeekAsync(double seconds,CancellationToken ct=default); Task SetVolumeAsync(double gain,CancellationToken ct=default);
}
public static class YouTubeUrl
{
 public static SourceValidationResult Normalize(string input){if(!Uri.TryCreate(input,UriKind.Absolute,out var uri))return new(false,"Enter a full YouTube URL."); var host=uri.Host.ToLowerInvariant(); if(host is not ("youtube.com" or "www.youtube.com" or "music.youtube.com" or "youtu.be"))return new(false,"Not a recognized YouTube URL."); var query=uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Split('=',2)).ToDictionary(x=>Uri.UnescapeDataString(x[0]),x=>x.Length>1?Uri.UnescapeDataString(x[1]):"",StringComparer.OrdinalIgnoreCase); query.TryGetValue("list",out var list);query.TryGetValue("v",out var video); if(host=="youtu.be") video=uri.AbsolutePath.Trim('/'); if(string.IsNullOrWhiteSpace(list)&&string.IsNullOrWhiteSpace(video))return new(false,"The URL does not contain a video or playlist identity."); var canonical=!string.IsNullOrWhiteSpace(list) ? $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(list)}" : $"https://www.youtube.com/watch?v={Uri.EscapeDataString(video!)}"; return new(true,"Official visible YouTube player required.",canonical);}
}
public sealed class YouTubeProvider : IPlaybackProvider
{
 public string ProviderId=>"youtube"; public PlaybackCapabilities Capabilities {get;}=new(true,false,false,true,true,true,true,true,false); public PlaybackSnapshot Snapshot {get;private set;}=new(false,0,null,null,ProviderHealth.Unavailable); public event EventHandler? StateChanged;
 public Task<SourceValidationResult> ValidateSourceAsync(string source,CancellationToken ct=default)=>Task.FromResult(YouTubeUrl.Normalize(source));
 public Task LoadAsync(Station station,CancellationToken ct=default){Snapshot=new(false,0,null,new(station.Name),ProviderHealth.Warning);StateChanged?.Invoke(this,EventArgs.Empty);return Task.CompletedTask;}
 public Task PlayAsync(CancellationToken ct=default)=>Task.CompletedTask; public Task PauseAsync(CancellationToken ct=default)=>Task.CompletedTask; public Task StopAsync(CancellationToken ct=default)=>Task.CompletedTask; public Task NextAsync(CancellationToken ct=default)=>Task.CompletedTask; public Task PreviousAsync(CancellationToken ct=default)=>Task.CompletedTask; public Task SeekAsync(double s,CancellationToken ct=default)=>Task.CompletedTask; public Task SetVolumeAsync(double v,CancellationToken ct=default)=>Task.CompletedTask; public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
public static class AppleMusicUrl
{
 public static SourceValidationResult Normalize(string input){if(!Uri.TryCreate(input,UriKind.Absolute,out var uri)||!uri.Host.EndsWith("music.apple.com",StringComparison.OrdinalIgnoreCase))return new(false,"Enter a music.apple.com album, playlist, song, or station URL.");var segments=uri.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries);if(segments.Length<3||segments[1] is not ("album" or "playlist" or "song" or "station"))return new(false,"The Apple Music URL type is not supported.");return new(true,"Apple Music source recognized; MusicKit authorization is required.",uri.GetLeftPart(UriPartial.Path));}
}
public static class SoundCloudUrl
{
 public static SourceValidationResult Normalize(string input){if(!Uri.TryCreate(input,UriKind.Absolute,out var uri)||!uri.Host.EndsWith("soundcloud.com",StringComparison.OrdinalIgnoreCase))return new(false,"Enter a soundcloud.com track or playlist URL.");if(uri.AbsolutePath.Trim('/').Length==0)return new(false,"The SoundCloud URL has no resource path.");return new(true,"SoundCloud source recognized; official public-client/OAuth setup is required.",uri.GetLeftPart(UriPartial.Path));}
}
public abstract class SetupRequiredProvider(string id,string providerName) : IPlaybackProvider
{
 public string ProviderId=>id; public PlaybackCapabilities Capabilities {get;}=new(false,false,false,false,false,false,false,false,false); public PlaybackSnapshot Snapshot {get;protected set;}=new(false,0,null,null,ProviderHealth.Unavailable); public event EventHandler? StateChanged;
 protected abstract SourceValidationResult Normalize(string source);
 public Task<SourceValidationResult> ValidateSourceAsync(string source,CancellationToken ct=default)=>Task.FromResult(Normalize(source));
 public Task LoadAsync(Station station,CancellationToken ct=default){Snapshot=new(false,0,null,new(station.Name),ProviderHealth.Unavailable);StateChanged?.Invoke(this,EventArgs.Empty);throw new InvalidOperationException($"{providerName} setup is required before playback.");}
 protected Task Unavailable()=>Task.FromException(new InvalidOperationException($"{providerName} setup is required before playback."));
 public Task PlayAsync(CancellationToken ct=default)=>Unavailable();public Task PauseAsync(CancellationToken ct=default)=>Unavailable();public Task StopAsync(CancellationToken ct=default)=>Unavailable();public Task NextAsync(CancellationToken ct=default)=>Unavailable();public Task PreviousAsync(CancellationToken ct=default)=>Unavailable();public Task SeekAsync(double seconds,CancellationToken ct=default)=>Unavailable();public Task SetVolumeAsync(double gain,CancellationToken ct=default)=>Unavailable();public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
public sealed class AppleMusicProvider : SetupRequiredProvider { public AppleMusicProvider():base("applemusic","Apple Music"){} protected override SourceValidationResult Normalize(string source)=>AppleMusicUrl.Normalize(source); }
public sealed class SoundCloudProvider : SetupRequiredProvider { public SoundCloudProvider():base("soundcloud","SoundCloud"){} protected override SourceValidationResult Normalize(string source)=>SoundCloudUrl.Normalize(source); }
public sealed class MpvLocator
{
 public string? Find(string? configured)
 {
   var candidates=new[]{configured,Path.Combine(AppContext.BaseDirectory,"mpv.exe"),Environment.GetEnvironmentVariable("MPV_PATH")}
     .Where(x=>!string.IsNullOrWhiteSpace(x));
   foreach(var path in candidates)if(File.Exists(path))return Path.GetFullPath(path!);
   foreach(var part in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries))
   {
     var path=Path.Combine(part,"mpv.exe");
     if(File.Exists(path))return Path.GetFullPath(path);
   }
   var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
   var links=Path.Combine(local,"Microsoft","WinGet","Links","mpv.exe");
   if(File.Exists(links))return links;
   var packages=Path.Combine(local,"Microsoft","WinGet","Packages");
   if(Directory.Exists(packages))
     foreach(var package in Directory.EnumerateDirectories(packages,"*mpv*"))
     {
       var path=Path.Combine(package,"mpv.exe");
       if(File.Exists(path))return path;
     }
   return null;
 }
}
public sealed class MpvProvider(MpvLocator locator, string? configuredPath=null, string? configuredAudioDevice=null) : IPlaybackProvider
{
 readonly string _pipeName="wardogs-radio-mpv-"+Guid.NewGuid().ToString("N"); Process? _process; NamedPipeClientStream? _pipe; StreamReader? _reader;
 readonly SemaphoreSlim _commands = new(1,1); long _nextRequestId;
 readonly SemaphoreSlim _volumeGate = new(1,1);
 double _requestedVolume=1; bool _firstPlayPending=true;
 public IReadOnlyList<string> LoadedFiles {get;private set;}=[];
 public int CurrentPlaylistIndex {get;private set;}
 public string ProviderId=>"mpv"; public PlaybackCapabilities Capabilities {get;}=new(false,true,true,true,true,true,true,false,true); public PlaybackSnapshot Snapshot {get;private set;}=new(false,0,null,null,ProviderHealth.Unknown); public event EventHandler? StateChanged;
 public Task<SourceValidationResult> ValidateSourceAsync(string source,CancellationToken ct=default)
 {
   if(Uri.TryCreate(source,UriKind.Absolute,out var u)&&u.Scheme is "http" or "https")return Task.FromResult(new SourceValidationResult(true,"Direct media URL accepted.",source.Trim()));
   if(Directory.Exists(source))
   {
     try { var files=LocalMediaPlaylist.FromDirectory(source,false);return Task.FromResult(new SourceValidationResult(true,$"Music folder contains {files.Count} supported files.",Path.GetFullPath(source))); }
     catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException){return Task.FromResult(new SourceValidationResult(false,error.Message));}
   }
   if(File.Exists(source))return Task.FromResult(new SourceValidationResult(true,"Local media source found.",Path.GetFullPath(source)));
   return Task.FromResult(new SourceValidationResult(false,"Local file/folder not found and URL is invalid."));
 }
 public async Task LoadAsync(Station station,CancellationToken ct=default)
 {
   _firstPlayPending=true;
   IReadOnlyList<string> files;
   var random=station.Shuffle && station.ShuffleSeed is { } seed?new Random(seed):null;
   if(station.PlaylistSongs?.Count>0)files=LocalMediaPlaylist.FromFiles(station.PlaylistSongs.Select(song=>song.Source),false);
   else if(station.PlaylistFiles?.Count>0)files=LocalMediaPlaylist.FromFiles(station.PlaylistFiles,station.Shuffle,random);
   else
   {
     var validation=await ValidateSourceAsync(station.Source,ct);
     if(!validation.IsValid){Snapshot=Snapshot with {Health=ProviderHealth.Failed};StateChanged?.Invoke(this,EventArgs.Empty);throw new InvalidOperationException(validation.Message);}
     var source=validation.CanonicalSource??station.Source;
     files=Directory.Exists(source)?LocalMediaPlaylist.FromDirectory(source,station.Shuffle,random):[source];
   }
   LoadedFiles=files;
   CurrentPlaylistIndex=0;
   await EnsureStarted(ct);
   if(!string.IsNullOrWhiteSpace(configuredAudioDevice))await SetAudioDeviceAsync(configuredAudioDevice,ct);
   await SetRepeatModeAsync(SongPlaylist.HasBoundaries(station)?StationRepeatMode.Off:station.EffectiveRepeatMode,ct);
   await Command(new[]{"loadfile",files[0],"replace"},ct);
   foreach(var file in files.Skip(1))await Command(new[]{"loadfile",file,"append"},ct);
   var loaded=false;
   for(var attempt=0;attempt<50;attempt++)
   {
     await Task.Delay(100,ct);
     try { loaded=!string.IsNullOrWhiteSpace(await GetStringProperty("path",ct)); if(loaded)break; }
     catch(InvalidOperationException) { }
   }
   if(!loaded)throw new InvalidOperationException("mpv accepted the source but did not load media. Check the file or stream URL.");
   string title;
   try { title=await GetStringProperty("media-title",ct)??station.Name; }
   catch(InvalidOperationException) { title=station.Name; }
   Snapshot=new(false,0,null,new(title),ProviderHealth.Ready);
   StateChanged?.Invoke(this,EventArgs.Empty);
 }
 async Task EnsureStarted(CancellationToken ct)
 {
   if(_process is {HasExited:false} && _pipe is {IsConnected:true})return;
   if(_process is {HasExited:false})_process.Kill(true);
   _reader?.Dispose();_reader=null;_pipe?.Dispose();_pipe=null;_process?.Dispose();_process=null;
   var exe=locator.Find(configuredPath);
   if(exe is null)throw new FileNotFoundException("mpv.exe was not found. Set it in Provider settings or add mpv to PATH.");
   _process=Process.Start(new ProcessStartInfo(exe,$"--no-config --idle=yes --no-video --force-window=no --terminal=no --pause=yes --input-ipc-server=\\\\.\\pipe\\{_pipeName}"){UseShellExecute=false,CreateNoWindow=true})
     ?? throw new IOException("mpv process could not start.");
   for(var i=0;i<20;i++)
   {
     ct.ThrowIfCancellationRequested();
     if(_process.HasExited)throw new IOException($"mpv exited before IPC connected (code {_process.ExitCode}).");
     try
     {
       _pipe=new NamedPipeClientStream(".",_pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
       await _pipe.ConnectAsync(250,ct);
       _reader=new StreamReader(_pipe,Encoding.UTF8,false,1024,leaveOpen:true);
       return;
     }
     catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
     catch(TimeoutException){_pipe?.Dispose();_pipe=null;await Task.Delay(100,ct);}
     catch(IOException){_pipe?.Dispose();_pipe=null;await Task.Delay(100,ct);}
   }
   throw new IOException("mpv IPC pipe did not become available.");
 }
 async Task<JsonElement> Command(object command,CancellationToken ct)
 {
   await _commands.WaitAsync(ct);
   try
   {
     using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
     timeout.CancelAfter(TimeSpan.FromSeconds(5));
     if(_pipe is null || !_pipe.IsConnected || _reader is null)throw new InvalidOperationException("mpv IPC is not connected.");
     var requestId=Interlocked.Increment(ref _nextRequestId);
     var json=JsonSerializer.Serialize(new{command,request_id=requestId});
     var bytes=Encoding.UTF8.GetBytes(json+"\n");
     await _pipe.WriteAsync(bytes,timeout.Token);await _pipe.FlushAsync(timeout.Token);
     while(true)
     {
       var line=await _reader.ReadLineAsync(timeout.Token)??throw new IOException("mpv disconnected before acknowledging the command.");
       using var response=JsonDocument.Parse(line);
       if(!response.RootElement.TryGetProperty("request_id",out var id)||id.GetInt64()!=requestId)continue;
       var error=response.RootElement.TryGetProperty("error",out var value)?value.GetString():null;
       if(error is not null && error!="success")throw new InvalidOperationException($"mpv rejected the command: {error}.");
       return response.RootElement.Clone();
     }
   }
   catch(OperationCanceledException) when(!ct.IsCancellationRequested) { throw new TimeoutException("mpv did not respond within five seconds."); }
   finally { _commands.Release(); }
 }
 async Task<string?> GetStringProperty(string property,CancellationToken ct)
 {
   var response=await Command(new[]{"get_property",property},ct);
   return response.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.String?data.GetString():null;
 }
 async Task<double?> GetNumberProperty(string property,CancellationToken ct)
 {
   var response=await Command(new[]{"get_property",property},ct);
   return response.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.Number?data.GetDouble():null;
 }
 async Task<bool?> GetBooleanProperty(string property,CancellationToken ct)
 {
   var response=await Command(new[]{"get_property",property},ct);
   return response.TryGetProperty("data",out var data)&&data.ValueKind is JsonValueKind.True or JsonValueKind.False?data.GetBoolean():null;
 }
 public async Task<IReadOnlyList<MpvAudioDevice>> ListAudioDevicesAsync(CancellationToken ct=default)
 {
   await EnsureStarted(ct);
   var response=await Command(new[]{"get_property","audio-device-list"},ct);
   if(!response.TryGetProperty("data",out var entries)||entries.ValueKind!=JsonValueKind.Array)
     throw new InvalidOperationException("mpv did not return an audio-device list.");
   return entries.EnumerateArray().Select(entry=>new MpvAudioDevice(
     entry.TryGetProperty("name",out var name)?name.GetString()??"":"",
     entry.TryGetProperty("description",out var description)?description.GetString()??"":""))
     .Where(x=>!string.IsNullOrWhiteSpace(x.Name)).ToList();
 }
 public async Task SetAudioDeviceAsync(string name,CancellationToken ct=default)
 {
   var devices=await ListAudioDevicesAsync(ct);
   if(!devices.Any(x=>x.Name==name))throw new InvalidOperationException($"MPV output device '{name}' is not available; no output was switched.");
   await Command(new[]{"set_property","audio-device",name},ct);
 }
 public async Task<PlaybackSnapshot> RefreshAsync(CancellationToken ct=default)
 {
   if(_pipe is not {IsConnected:true})throw new InvalidOperationException("mpv IPC is not connected.");
   double? position=null,duration=null;
   try{position=await GetNumberProperty("time-pos",ct);}catch(InvalidOperationException){}
   try{duration=await GetNumberProperty("duration",ct);}catch(InvalidOperationException){}
   try{CurrentPlaylistIndex=(int)(await GetNumberProperty("playlist-pos",ct)??CurrentPlaylistIndex);}catch(InvalidOperationException){}
   var pause=await GetBooleanProperty("pause",ct)??true;
   var idle=await GetBooleanProperty("idle-active",ct)??false;
   string? title=null;
   try{title=await GetStringProperty("media-title",ct);}catch(InvalidOperationException){}
   Snapshot=new(!pause&&!idle,position??0,duration,title is null?Snapshot.Track:new(title),ProviderHealth.Ready);
   StateChanged?.Invoke(this,EventArgs.Empty);
   return Snapshot;
 }
 public async Task PlayAsync(CancellationToken ct=default)
 {
   if(await GetBooleanProperty("idle-active",ct)==true && LoadedFiles.Count>0)
       await SelectTrackAsync(0,0,ct);
   if(_firstPlayPending)
   {
     // Opening an audio endpoint can produce a startup blip. Start at digital silence,
     // then ease up to the requested gain during the first 320 ms of playback.
     // A player prepared for a crossfade has already been explicitly muted at zero.
     // The fade must therefore also own the corresponding unmute transition; changing
     // only the volume property leaves mpv playing silently.
     var target=Math.Clamp(_requestedVolume,0,1);
     await Command(new object[]{"set_property","volume",0},ct);
     await Command(new[]{"set_property","mute","yes"},ct);
     await Command(new[]{"set_property","pause","no"},ct);
     for(var step=1;step<=8;step++)
     {
       await Task.Delay(40,ct);
       if(step==1)
       {
         await Command(new[]{"set_property","mute","no"},ct);
       }
       await Command(new object[]{"set_property","volume",target*100*step/8},ct);
     }
     _firstPlayPending=false;
   }
   else await Command(new[]{"set_property","pause","no"},ct);
   Snapshot=Snapshot with{IsPlaying=true};StateChanged?.Invoke(this,EventArgs.Empty);
 }
 public async Task SetRepeatModeAsync(StationRepeatMode mode,CancellationToken ct=default)
 {
   await Command(new[]{"set_property","loop-file","no"},ct);
   await Command(new[]{"set_property","loop-playlist",mode==StationRepeatMode.Playlist?"inf":"no"},ct);
   if(mode==StationRepeatMode.Track)await Command(new[]{"set_property","loop-file","inf"},ct);
 }
 public async Task PauseAsync(CancellationToken ct=default){await Command(new[]{"set_property","pause","yes"},ct);Snapshot=Snapshot with{IsPlaying=false};StateChanged?.Invoke(this,EventArgs.Empty);}
 public async Task StopAsync(CancellationToken ct=default){await Command(new[]{"stop"},ct);Snapshot=Snapshot with{IsPlaying=false};StateChanged?.Invoke(this,EventArgs.Empty);}
 public async Task NextAsync(CancellationToken ct=default){await Command(new[]{"playlist-next"},ct);}
 public async Task PreviousAsync(CancellationToken ct=default)
 {
   double? position;
   try { position=await GetNumberProperty("time-pos",ct); }
   catch(InvalidOperationException) { position=null; }
   if(position>5) await SeekAsync(0,ct);
   else await Command(new[]{"playlist-prev"},ct);
 }
 public async Task SeekAsync(double s,CancellationToken ct=default){await Command(new object[]{"seek",s,"absolute"},ct);}
 public async Task<double> ReadVolumeAsync(CancellationToken ct=default) =>
   (await GetNumberProperty("volume",ct)??throw new InvalidOperationException("mpv did not report its volume."))/100;
 public async Task<bool> ReadMuteAsync(CancellationToken ct=default) =>
   await GetBooleanProperty("mute",ct)??throw new InvalidOperationException("mpv did not report its mute state.");
 public async Task SelectTrackAsync(int index,double seconds,CancellationToken ct=default)
 {
   if(index<0||index>=LoadedFiles.Count)throw new ArgumentOutOfRangeException(nameof(index));
   await Command(new object[]{"set_property","playlist-pos",index},ct);
   for(var attempt=0;attempt<50;attempt++)
   {
     ct.ThrowIfCancellationRequested();
     try
     {
       var path=await GetStringProperty("path",ct);
       if(string.Equals(path,LoadedFiles[index],StringComparison.OrdinalIgnoreCase))break;
     }
     catch(InvalidOperationException){}
     if(attempt==49)throw new TimeoutException("mpv did not switch to the requested station track.");
     await Task.Delay(50,ct);
   }
   CurrentPlaylistIndex=index;
   if(seconds>0)
   {
     for(var attempt=0;attempt<40;attempt++)
     {
       ct.ThrowIfCancellationRequested();
       try
       {
         if(await GetNumberProperty("duration",ct) is > 0)
         {
           await SeekAsync(seconds,ct);
           break;
         }
       }
       catch(InvalidOperationException) when(attempt<39){}
       if(attempt==39)throw new TimeoutException("mpv did not become seekable after selecting the station track.");
       await Task.Delay(50,ct);
     }
   }
   await RefreshAsync(ct);
 }
 public async Task SetVolumeAsync(double v,CancellationToken ct=default)
 {
   _requestedVolume=Math.Clamp(v,0,1);
   await _volumeGate.WaitAsync(ct);
   try
   {
     var target=_requestedVolume;
     await Command(new object[]{"set_property","volume",target*100},ct);
     // Keep mute explicit for every gain write. Some WASAPI endpoints do not
     // reliably retain a cached mute state across a device start or crossfade.
     await Command(new[]{"set_property","mute",target<=.0001?"yes":"no"},ct);
   }
   finally{_volumeGate.Release();}
 }
 public async ValueTask DisposeAsync()
 {
   try{if(_pipe is {IsConnected:true})await Command(new[]{"quit"},CancellationToken.None);}catch{}
   _reader?.Dispose();_pipe?.Dispose();
   if(_process is {HasExited:false})
   {
     try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));await _process.WaitForExitAsync(timeout.Token);}
     catch(OperationCanceledException){if(!_process.HasExited)_process.Kill(true);}
   }
   _process?.Dispose();_commands.Dispose();_volumeGate.Dispose();
 }
}
public sealed record ExternalSourceIdentity(string? SourceAppId,string? ExecutablePath,string? ExecutableName,string? SessionId,bool IncludeChildProcesses=false);
public sealed record ExternalSessionDescriptor(string SessionId,string ApplicationName,string? SourceAppId,int ProcessId,string? ExecutablePath,bool CanPlay,bool CanPause,bool CanNext,bool CanPrevious,bool CanSeek,bool CanTimeline,bool CanCapture,bool Ambiguous,TrackMetadata? Track=null,double PositionSeconds=0,double? DurationSeconds=null);
public interface IExternalMediaSessionBackend
{
 Task<IReadOnlyList<ExternalSessionDescriptor>> DiscoverAsync(CancellationToken ct=default);
 Task<bool> TryCommandAsync(string sessionId,string command,object? value=null,CancellationToken ct=default);
}
/// <summary>Uses Windows' Global System Media Transport Controls manager through the installed WinRT projection.</summary>
public sealed class PowerShellMediaSessionBackend : IExternalMediaSessionBackend
{
 const string WinRtPrelude = "Add-Type -Path 'C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\System.Runtime.WindowsRuntime.dll';$null=[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime];function Await-WinRt($op,[Type]$type){$m=[System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object {$_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1}|Select-Object -First 1;$m.MakeGenericMethod($type).Invoke($null,@(,$op)).Result};";
 static string Quoted(string value)=>Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
 static async Task<string> InvokeAsync(string script,CancellationToken ct){var encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';"+script));using var p=Process.Start(new ProcessStartInfo("powershell.exe",$"-NoProfile -NonInteractive -EncodedCommand {encoded}"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true})??throw new InvalidOperationException("Windows PowerShell is unavailable.");var output=await p.StandardOutput.ReadToEndAsync(ct);var error=await p.StandardError.ReadToEndAsync(ct);await p.WaitForExitAsync(ct);if(p.ExitCode!=0)throw new InvalidOperationException(error.Trim());return output;}
 public async Task<IReadOnlyList<ExternalSessionDescriptor>> DiscoverAsync(CancellationToken ct=default)
 {
   var script=WinRtPrelude+"$m=Await-WinRt ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]);$rows=@();if($null -ne $m){foreach($s in $m.GetSessions()){$p=$s.GetPlaybackInfo();$t=$s.GetTimelineProperties();$media=Await-WinRt ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties]);$rows+=[pscustomobject]@{Id=$s.SourceAppUserModelId;App=$s.SourceAppUserModelId;Title=$media.Title;Artist=$media.Artist;CanPlay=$p.Controls.IsPlayEnabled;CanPause=$p.Controls.IsPauseEnabled;CanNext=$p.Controls.IsNextEnabled;CanPrevious=$p.Controls.IsPreviousEnabled;CanSeek=$p.Controls.IsPlaybackPositionEnabled;Position=$t.Position.TotalSeconds;Duration=($t.EndTime-$t.StartTime).TotalSeconds}}};$rows|ConvertTo-Json -Compress";
   var json=(await InvokeAsync(script,ct)).Trim();if(string.IsNullOrWhiteSpace(json))return [];using var document=JsonDocument.Parse(json);IEnumerable<JsonElement> rows=document.RootElement.ValueKind==JsonValueKind.Array?document.RootElement.EnumerateArray().ToArray():[document.RootElement];return rows.Select(x=>{string Get(string n)=>x.TryGetProperty(n,out var v)&&v.ValueKind!=JsonValueKind.Null?v.GetString()??"":"";bool Flag(string n)=>x.TryGetProperty(n,out var v)&&v.GetBoolean();double Number(string n)=>x.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.Number?v.GetDouble():0;return new ExternalSessionDescriptor(Get("Id"),Get("App"),Get("App"),0,null,Flag("CanPlay"),Flag("CanPause"),Flag("CanNext"),Flag("CanPrevious"),Flag("CanSeek"),Flag("CanSeek"),false,false,new TrackMetadata(Get("Title"),Get("Artist")),Number("Position"),Number("Duration"));}).ToList();
 }
 public async Task<bool> TryCommandAsync(string sessionId,string command,object? value=null,CancellationToken ct=default)
 {
   if(command is not ("play" or "pause" or "stop" or "next" or "previous" or "seek"))return false;var id=Quoted(sessionId);var seek=value is double seconds?((long)TimeSpan.FromSeconds(seconds).Ticks).ToString(System.Globalization.CultureInfo.InvariantCulture):"0";var method=command switch{"play"=>"TryPlayAsync","pause"=>"TryPauseAsync","stop"=>"TryStopAsync","next"=>"TrySkipNextAsync","previous"=>"TrySkipPreviousAsync",_=>"TryChangePlaybackPositionAsync("+seek+")"};var script=WinRtPrelude+"$id=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('"+id+"'));$m=Await-WinRt ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]);$s=$m.GetSessions()|Where-Object {$_.SourceAppUserModelId -eq $id}|Select-Object -First 1;if($null -eq $s){'false'}else{[string](Await-WinRt ($s."+method+") ([bool]))}";return bool.TryParse((await InvokeAsync(script,ct)).Trim(),out var result)&&result;
 }
}
public sealed record ProcessLoopbackActivationResult(bool Available,string Detail);
/// <summary>Native Windows process-loopback activation probe. It never selects the system mix endpoint.</summary>
public sealed class ProcessLoopbackActivator
{
 const string VirtualProcessLoopbackDevice="VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK"; const ushort VtBlob=65;
 [StructLayout(LayoutKind.Explicit)] struct PropVariant { [FieldOffset(0)] public ushort Vt; [FieldOffset(8)] public int Size; [FieldOffset(16)] public IntPtr Data; }
 [StructLayout(LayoutKind.Sequential)] struct ActivationParams { public int ActivationType; public uint TargetProcessId; public int LoopbackMode; }
 [ComImport,Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IActivationOperation { [PreserveSig] int GetActivateResult(out int activateResult,[MarshalAs(UnmanagedType.IUnknown)]out object? activatedInterface); }
 [ComImport,Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IActivationCompletionHandler { void ActivateCompleted(IActivationOperation operation); }
 [ComVisible(true)] sealed class Completion(TaskCompletionSource<(int Hr,object? Client)> source):IActivationCompletionHandler { public void ActivateCompleted(IActivationOperation operation){try{var call=operation.GetActivateResult(out var result,out var client);source.TrySetResult((call!=0?call:result,client));}catch(Exception ex){source.TrySetException(ex);}} }
 [DllImport("Mmdevapi.dll",CharSet=CharSet.Unicode,PreserveSig=true)] static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath,ref Guid riid,IntPtr activationParams,IActivationCompletionHandler completionHandler,out IActivationOperation operation);
 public async Task<ProcessLoopbackActivationResult> TryActivateAsync(uint processId,CancellationToken ct=default)
 {
   var payload=Marshal.AllocCoTaskMem(Marshal.SizeOf<ActivationParams>());var variant=Marshal.AllocCoTaskMem(Marshal.SizeOf<PropVariant>());IActivationOperation? operation=null;object? client=null;
   try{Marshal.StructureToPtr(new ActivationParams{ActivationType=1,TargetProcessId=processId,LoopbackMode=0},payload,false);Marshal.StructureToPtr(new PropVariant{Vt=VtBlob,Size=Marshal.SizeOf<ActivationParams>(),Data=payload},variant,false);var source=new TaskCompletionSource<(int Hr,object? Client)>(TaskCreationOptions.RunContinuationsAsynchronously);var handler=new Completion(source);var iid=new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");var hr=ActivateAudioInterfaceAsync(VirtualProcessLoopbackDevice,ref iid,variant,handler,out operation);if(hr<0)return new(false,$"ActivateAudioInterfaceAsync returned 0x{hr:X8}.");using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(5));var result=await source.Task.WaitAsync(timeout.Token);client=result.Client;if(result.Hr<0)return new(false,$"Process-loopback activation returned 0x{result.Hr:X8}.");return new(true,"Process-specific WASAPI loopback activation succeeded.");}catch(OperationCanceledException){return new(false,"Process-loopback activation timed out.");}catch(Exception ex){return new(false,ex.Message);}finally{if(client is not null&&Marshal.IsComObject(client))Marshal.ReleaseComObject(client);if(operation is not null&&Marshal.IsComObject(operation))Marshal.ReleaseComObject(operation);Marshal.FreeCoTaskMem(variant);Marshal.FreeCoTaskMem(payload);}
 }
}
public static class ExternalSessionMatcher
{
 public static ExternalSessionDescriptor? Resolve(ExternalSourceIdentity identity,IEnumerable<ExternalSessionDescriptor> sessions)=>sessions.Where(s=>(identity.SourceAppId is null||s.SourceAppId==identity.SourceAppId)&&(identity.ExecutablePath is null||string.Equals(s.ExecutablePath,identity.ExecutablePath,StringComparison.OrdinalIgnoreCase))&&(identity.ExecutableName is null||string.Equals(Path.GetFileName(s.ExecutablePath),identity.ExecutableName,StringComparison.OrdinalIgnoreCase))).OrderBy(s=>s.Ambiguous).FirstOrDefault();
}
public sealed class ExternalAudioProvider(IExternalMediaSessionBackend backend,ExternalSourceIdentity identity) : IPlaybackProvider
{
 ExternalSessionDescriptor? _session; public string ProviderId=>"external-audio"; public PlaybackCapabilities Capabilities {get;private set;}=new(false,false,false,false,false,false,false,false,false); public PlaybackSnapshot Snapshot {get;private set;}=new(false,0,null,null,ProviderHealth.Unknown); public event EventHandler? StateChanged;
 public async Task<SourceValidationResult> ValidateSourceAsync(string source,CancellationToken ct=default){var session=ExternalSessionMatcher.Resolve(identity,await backend.DiscoverAsync(ct));return session is null?new(false,"Application not running or media session unavailable."):session.Ambiguous?new(false,"Ambiguous browser/media source; choose a specific session."):new(true,"External media session available.",session.SessionId);}
 public async Task LoadAsync(Station station,CancellationToken ct=default){_session=ExternalSessionMatcher.Resolve(identity,await backend.DiscoverAsync(ct));if(_session is null){Snapshot=new(false,0,null,null,ProviderHealth.Unavailable);StateChanged?.Invoke(this,EventArgs.Empty);return;} Capabilities=new(false,_session.CanCapture,_session.CanCapture,_session.CanSeek,_session.CanNext||_session.CanPrevious,true,_session.Track is not null,_session.CanTimeline&&_session.CanSeek,_session.CanCapture);Snapshot=new(false,_session.PositionSeconds,_session.DurationSeconds,_session.Track,_session.CanCapture?ProviderHealth.Ready:ProviderHealth.Warning);StateChanged?.Invoke(this,EventArgs.Empty);}
 async Task Command(string command,object? value,CancellationToken ct){if(_session is null)throw new InvalidOperationException("External application is unavailable.");if(!await backend.TryCommandAsync(_session.SessionId,command,value,ct))throw new InvalidOperationException($"External application does not support {command}.");}
 public Task PlayAsync(CancellationToken ct=default)=>Command("play",null,ct); public Task PauseAsync(CancellationToken ct=default)=>Command("pause",null,ct); public Task StopAsync(CancellationToken ct=default)=>Command("stop",null,ct); public Task NextAsync(CancellationToken ct=default)=>Command("next",null,ct);
 public async Task PreviousAsync(CancellationToken ct=default)
 {
   if(_session is null)throw new InvalidOperationException("External application is unavailable.");
   var current=(await backend.DiscoverAsync(ct)).FirstOrDefault(x=>x.SessionId==_session.SessionId);
   if(current is {CanSeek:true,PositionSeconds:>5})await SeekAsync(0,ct);
   else await Command("previous",null,ct);
 }
 public Task SeekAsync(double seconds,CancellationToken ct=default)=>Command("seek",seconds,ct); public Task SetVolumeAsync(double gain,CancellationToken ct=default)=>Command("broadcast-gain",gain,ct); public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
