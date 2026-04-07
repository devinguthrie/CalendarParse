// Captures cx at peak zoom (before zoom-out), stores in output.cxMid
var m = (maestro.copiedText || '').match(/cx=(\d+)/);
output.cxMid = m ? parseInt(m[1], 10) : -1;
console.log('[zoom-xlock] cx at peak zoom (cxMid) = ' + output.cxMid);
if (output.cxMid === -1) throw 'Could not read cx from PositionDebugLabel at peak zoom (text was: ' + JSON.stringify(maestro.copiedText || '') + ')';
