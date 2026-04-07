// Reads cx from PositionDebugLabel after copyTextFrom, stores in output.cxBefore
var text = maestro.copiedText || '';
var m = text.match(/cx=(\d+)/);
output.cxBefore = m ? parseInt(m[1], 10) : -1;
console.log('[zoom-xlock] cx BEFORE zoom = ' + output.cxBefore);
if (output.cxBefore === -1) throw 'Could not read cx from PositionDebugLabel (text was: ' + JSON.stringify(text) + ')';
