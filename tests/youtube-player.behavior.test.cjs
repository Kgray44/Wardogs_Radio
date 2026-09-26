const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '..', 'src', 'WardogsRadio.App', 'youtube-player.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)?.[1];
assert.ok(script, 'Embedded YouTube controller script exists');

function harness(repeat, list = '') {
  const calls = [];
  const messages = [];
  let player;
  let tick;
  class Player {
    constructor(_, options) {
      this.events = options.events;
      this.state = -1;
      this.position = 100;
      player = this;
    }
    setLoop(value) { calls.push(['loop', value]); }
    getPlaylist() { return list ? ['abcde', 'fghij'] : []; }
    getPlaylistIndex() { return 1; }
    getCurrentTime() { return this.position; }
    getDuration() { return 600; }
    getPlayerState() { return this.state; }
    getVideoData() { return { video_id: 'abcde' }; }
    loadVideoById(value) { calls.push(['load', value]); }
    playVideoAt(index) { calls.push(['playAt', index]); }
    playVideo() { calls.push(['play']); }
    pauseVideo() { calls.push(['pause']); }
    seekTo(seconds) { calls.push(['seek', seconds]); }
  }
  const YT = { Player, PlayerState: { ENDED: 0, PLAYING: 1 } };
  const window = { chrome: { webview: { postMessage: value => messages.push(JSON.parse(value)) } } };
  const context = {
    window, YT, URLSearchParams,
    location: { search: `?v=abcde&repeat=${repeat}&instance=probe${list ? `&list=${list}` : ''}`, origin: 'https://wardogs-radio.example' },
    document: { createElement: () => ({}), head: { appendChild() {} } },
    setInterval(callback) { tick = callback; },
    setTimeout(callback) { callback(); }
  };
  vm.runInNewContext(script, context);
  window.onYouTubeIframeAPIReady();
  player.events.onReady();
  return { calls, messages, player, window, tick: () => tick() };
}

{
  const test = harness('playlist');
  test.player.state = 0;
  test.player.events.onStateChange({ data: 0 });
  assert.ok(test.calls.some(call => call[0] === 'load' && call[1].startSeconds === 0));
  assert.ok(test.messages.some(message => message.type === 'state' && message.detail === '0' && message.instance === 'probe'));
}
{
  const test = harness('off');
  test.player.state = 0;
  test.player.events.onStateChange({ data: 0 });
  assert.equal(test.calls.filter(call => call[0] === 'load').length, 0);
  test.window.wardogs.play();
  assert.equal(test.calls.filter(call => call[0] === 'load').length, 1);
}
{
  const test = harness('track', 'playlistId');
  test.player.state = 0;
  test.player.events.onStateChange({ data: 0 });
  assert.ok(test.calls.some(call => call[0] === 'playAt' && call[1] === 1));
  test.window.wardogs.repeat('playlist');
  assert.ok(test.calls.some(call => call[0] === 'loop' && call[1] === true));
}
{
  const test = harness('playlist');
  test.window.wardogs.setSegments([
    { source: 'abcde', start: 0, end: 120 },
    { source: 'abcde', start: 120, end: null }
  ], 0);
  test.player.state = 1;
  test.player.position = 120.5;
  test.tick();
  assert.ok(test.calls.some(call => call[0] === 'seek' && call[1] === 120), 'boundary advances to next named song');
  test.window.wardogs.selectSegment(0);
  assert.ok(test.calls.some(call => call[0] === 'seek' && call[1] === 0), 'playlist order can seek backward in same video');
}
console.log('YouTube end/repeat and named-song controls passed.');
