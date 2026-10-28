export default {
  extends: ['@commitlint/config-conventional'],
  rules: {
    'scope-enum': [1, 'always', ['dock', 'ui', 'shell', 'trash', 'settings', 'build', 'ci', 'docs', 'deps']],
  },
};
