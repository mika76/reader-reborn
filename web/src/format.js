import DOMPurify from 'dompurify'

// Links in feed content open in a new tab; images load lazily.
DOMPurify.addHook('afterSanitizeAttributes', node => {
  if (node.tagName === 'A' && node.hasAttribute('href')) {
    node.setAttribute('target', '_blank')
    node.setAttribute('rel', 'noopener noreferrer')
  }
  if (node.tagName === 'IMG') node.setAttribute('loading', 'lazy')
})

export const sanitize = html =>
  DOMPurify.sanitize(html || '', { FORBID_TAGS: ['style', 'form', 'input', 'button'], FORBID_ATTR: ['style', 'class', 'id'] })

const time = d => d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })

// Reader's dates: "10:42 AM" today, "Oct 21" this year, "10/21/12" before that.
export function shortDate(unix) {
  const d = new Date(unix * 1000)
  const now = new Date()
  if (d.toDateString() === now.toDateString()) return time(d)
  if (d.getFullYear() === now.getFullYear()) return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
  return d.toLocaleDateString('en-US', { month: 'numeric', day: 'numeric', year: '2-digit' })
}

export function longDate(unix) {
  const d = new Date(unix * 1000)
  return `${d.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' })} ${time(d)}`
}

// Feed attachments: what to show for one, from its MIME type or else its file extension
export function enclosureKind(enclosure) {
  const type = enclosure.type || ''
  const ext = enclosure.url.split(/[?#]/)[0].split('.').pop().toLowerCase()
  if (type.startsWith('audio/') || ['mp3', 'm4a', 'ogg', 'oga', 'opus', 'wav', 'aac'].includes(ext)) return 'audio'
  if (type.startsWith('video/') || ['mp4', 'm4v', 'webm', 'mov'].includes(ext)) return 'video'
  if (type.startsWith('image/') || ['jpg', 'jpeg', 'png', 'gif', 'webp'].includes(ext)) return 'image'
  return 'file'
}

export function formatBytes(n) {
  if (!n) return ''
  const units = ['bytes', 'KB', 'MB', 'GB']
  let i = 0
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++ }
  return `${i ? n.toFixed(1) : n} ${units[i]}`
}

export function enclosureLabel(enclosure) {
  const kind = enclosureKind(enclosure)
  const name = kind === 'file' ? decodeURIComponent(enclosure.url.split(/[?#]/)[0].split('/').pop() || 'file') : `Original ${kind}`
  const size = formatBytes(enclosure.length)
  return size ? `${name} (${size})` : name
}
