/* Local ESLint plugin wrapper to expose local rules as 'local/*' */
module.exports = {
  rules: {
    'pf-no-unguarded-console': require('./pf-no-unguarded-console')
  }
};
