using Android.Content;
using Android.Views;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.UI;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Platform;

namespace MioReel.Desktop.Controls;

/// <summary>
/// Hosts the native Media3 <see cref="PlayerView"/> inside Avalonia.
/// Decoding is delegated to Android MediaCodec (hardware accelerated when available),
/// so no FFmpeg/native video pipeline is required on the TV package.
/// </summary>
public sealed class Media3PlayerView : NativeControlHost
{
    private PlayerView? _playerView;
    private IExoPlayer? _player;

    public bool IsReady => _player is not null && _playerView is not null;

    public long PositionMs => _player?.CurrentPosition ?? 0;

    public long DurationMs => _player?.Duration ?? 0;

    public int PlaybackState => _player?.PlaybackState ?? 0;

    public bool IsPlaying => _player?.IsPlaying == true;

    public string? PlayerErrorMessage => _player?.PlayerError?.Message;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var context = (Context?)MainActivity.Current ?? global::Android.App.Application.Context;

        _player = new ExoPlayerBuilder(context).Build();

        var inflater = LayoutInflater.From(context)
            ?? throw new InvalidOperationException("Unable to resolve Android LayoutInflater.");
        _playerView = inflater.Inflate(MioReel.Desktop.Resource.Layout.player_view, null) as PlayerView
            ?? throw new InvalidOperationException("Unable to inflate the Media3 PlayerView layout.");

        _playerView.Player = _player;
        // The TV UI owns D-pad focus; the controller would steal it from Avalonia.
        _playerView.UseController = false;
        _playerView.Focusable = false;
        _playerView.FocusableInTouchMode = false;
        _playerView.SetBackgroundColor(global::Android.Graphics.Color.Black);

        return new AndroidViewControlHandle(_playerView);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ReleasePlayer();
        base.DestroyNativeControlCore(control);
    }

    public void Load(string url, long startPositionMs = 0, bool play = true, double speed = 1.0, double volume = 1.0)
    {
        if (_player is null || _playerView is null)
            return;

        _player.SetMediaItem(MediaItem.FromUri(url), Math.Max(0, startPositionMs));
        _player.SetPlaybackSpeed((float)Math.Clamp(speed, 0.25, 4.0));
        _player.Volume = (float)Math.Clamp(volume, 0.0, 1.0);
        _player.Prepare();
        _player.PlayWhenReady = play;
    }

    public void SetPlaying(bool playing)
    {
        if (_player is null) return;
        _player.PlayWhenReady = playing;
    }

    public void SeekTo(long positionMs)
    {
        if (_player is null) return;
        var duration = _player.Duration;
        var max = duration > 0 ? duration : long.MaxValue;
        _player.SeekTo(Math.Clamp(positionMs, 0, max));
    }

    public void SeekBy(long deltaMs) => SeekTo(PositionMs + deltaMs);

    public void SetPlaybackSpeed(double speed)
    {
        _player?.SetPlaybackSpeed((float)Math.Clamp(speed, 0.25, 4.0));
    }

    public void SetVolume(double volume)
    {
        if (_player is not null)
            _player.Volume = (float)Math.Clamp(volume, 0.0, 1.0);
    }

    public void Stop()
    {
        if (_player is null) return;
        _player.Pause();
        _player.ClearMediaItems();
    }

    public void ReleasePlayer()
    {
        if (_playerView is not null)
        {
            _playerView.Player = null;
            _playerView.Dispose();
            _playerView = null;
        }

        if (_player is not null)
        {
            _player.Release();
            _player.Dispose();
            _player = null;
        }
    }
}