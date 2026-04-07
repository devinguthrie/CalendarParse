// Reads cx AFTER first zoom-in pair. Asserts drift from cxBefore <= 5 px.
var text = maestro.copiedText || '';
var m = text.match(/cx=(\d+)/);
var cxAfter = m ? parseInt(m[1], 10) : -1;
var cxBefore = output.cxBefore;

if (cxAfter === -1) throw 'Could not read cx from PositionDebugLabel after zoom-in (text was: ' + JSON.stringify(text) + ')';

var drift = Math.abs(cxBefore - cxAfter);

console.log('[zoom-xlock] cx BEFORE=' + cxBefore + ' AFTER=' + cxAfter + ' drift=' + drift);

if (drift > 5) {
  throw 'Zoom X-lock FAILED: cx drifted from ' + cxBefore + ' -> ' + cxAfter
      + ' (' + drift + ' px). See screenshots zoom-xlock-01-before / zoom-xlock-02-after';
}
output.cxAfterZoomIn = cxAfter;
console.log('[zoom-xlock] PASS: zoom-in lock held (drift=' + drift + ' px)');
