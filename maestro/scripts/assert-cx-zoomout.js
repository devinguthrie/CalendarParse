// Asserts cx AFTER zoom-out did not drift more than 5 px from cxMid.
var text = maestro.copiedText || '';
var m = text.match(/cx=(\d+)/);
var cxOut = m ? parseInt(m[1], 10) : -1;

if (cxOut === -1) throw 'Could not read cx from PositionDebugLabel after zoom-out (text was: ' + JSON.stringify(text) + ')';

var drift = Math.abs(output.cxMid - cxOut);

console.log('[zoom-xlock] zoom-out cx BEFORE=' + output.cxMid + ' AFTER=' + cxOut + ' drift=' + drift);

if (drift > 5) {
  throw 'Zoom X-lock zoom-OUT FAILED: cx drifted from ' + output.cxMid + ' -> ' + cxOut
      + ' (' + drift + ' px). See screenshot zoom-xlock-03-zoom-out';
}
console.log('[zoom-xlock] PASS: zoom-out lock held (drift=' + drift + ' px)');
