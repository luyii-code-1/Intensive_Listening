(() => {
  const routes = {
    quickstart: 'getting-started', 'web-player': 'web-player', prepare: 'installation',
    'first-run': 'first-run', 'api-setup': 'transcription', teacher: 'authoring',
    agent: 'agents', student: 'listening', settings: 'settings', help: 'faq'
  };
  const section = window.location.hash.slice(1);
  const page = Object.hasOwn(routes, section) ? routes[section] + '.html' : '';
  window.location.replace('/docs/' + page);
})();
