async function request(method, url, body, raw = false) {
  const res = await fetch(url, {
    method,
    headers: body === undefined || raw ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : raw ? body : JSON.stringify(body),
  })
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`
    try { message = (await res.json()).error || message } catch { /* not JSON */ }
    throw new Error(message)
  }
  return res.status === 204 ? null : res.json()
}

export const get = url => request('GET', url)
export const post = (url, body) => request('POST', url, body ?? {})
export const postRaw = (url, body) => request('POST', url, body, true)
export const patch = (url, body) => request('PATCH', url, body)
export const del = url => request('DELETE', url)
