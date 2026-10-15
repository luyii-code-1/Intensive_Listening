const repository = 'luyii-code-1/Intensive_Listening';
const redirectHosts = new Set(['github.com', 'release-assets.githubusercontent.com']);

function failure(status, message) {
  return new Response(message, { status, headers: { 'Cache-Control': 'no-store' } });
}

export async function onRequest({ request }) {
  if (!['GET', 'HEAD'].includes(request.method)) {
    return new Response('Method not allowed', {
      status: 405, headers: { Allow: 'GET, HEAD', 'Cache-Control': 'no-store' },
    });
  }
  const path = new URL(request.url).pathname;
  const match = /^\/downloads\/(v2\.\d+\.\d+)\/(Intensive-Listening-(\d+\.\d+\.\d+)-(?:win-x64-Setup\.exe|osx-arm64\.dmg))$/.exec(path);
  if (!match || match[1] !== `v${match[3]}`) return failure(404, 'Unknown download');

  const headers = new Headers({ 'Accept-Encoding': 'identity' });
  for (const name of ['Range', 'If-Range', 'If-None-Match', 'If-Modified-Since']) {
    if (request.headers.has(name)) headers.set(name, request.headers.get(name));
  }
  let url = new URL(`https://github.com/${repository}/releases/download/${match[1]}/${match[2]}`);
  try {
    for (let redirects = 0; redirects <= 5; redirects++) {
      const upstream = await fetch(url, { method: request.method, headers, redirect: 'manual', signal: request.signal });
      if ([301, 302, 303, 307, 308].includes(upstream.status)) {
        const location = upstream.headers.get('Location');
        await upstream.body?.cancel();
        if (!location) return failure(502, 'Invalid download redirect');
        url = new URL(location, url);
        if (url.protocol !== 'https:' || !redirectHosts.has(url.hostname) ||
            url.port || url.username || url.password) return failure(502, 'Invalid download origin');
        continue;
      }
      if (![200, 206, 304, 416].includes(upstream.status)) {
        await upstream.body?.cancel();
        return failure(upstream.status === 404 ? 404 : 502, 'Download unavailable');
      }
      const responseHeaders = new Headers();
      for (const name of ['Content-Type', 'Content-Length', 'Content-Range', 'Accept-Ranges', 'ETag', 'Last-Modified']) {
        if (upstream.headers.has(name)) responseHeaders.set(name, upstream.headers.get(name));
      }
      responseHeaders.set('Content-Disposition', `attachment; filename="${match[2]}"`);
      responseHeaders.set('X-Content-Type-Options', 'nosniff');
      responseHeaders.set('Cache-Control', upstream.status === 200 && !headers.has('Range')
        ? 'public, max-age=3600' : 'no-store');
      return new Response(request.method === 'HEAD' ? null : upstream.body, {
        status: upstream.status, headers: responseHeaders,
      });
    }
    return failure(502, 'Too many download redirects');
  } catch {
    return failure(502, 'Download origin unavailable');
  }
}
