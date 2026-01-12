/* Local ESLint plugin wrapper to expose local rules as 'local/*' */
import pfNoUngaurdedConsole from './pf-no-unguarded-console.js'
import pfNoRawHtmlControls from './pf-no-raw-html-controls.js'
import noHardcodedColors from './no-hardcoded-colors.js'

export default {
  rules: {
    'pf-no-unguarded-console': pfNoUngaurdedConsole,
    'pf-no-raw-html-controls': pfNoRawHtmlControls,
    'no-hardcoded-colors': noHardcodedColors
  }
};
