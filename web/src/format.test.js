import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { sanitize, shortDate, longDate, enclosureKind, enclosureLabel, formatBytes } from './format.js'

const unix = s => Math.floor(new Date(s).getTime() / 1000)

describe('shortDate', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date(2024, 5, 15, 18, 0))
  })
  afterEach(() => vi.useRealTimers())

  it('shows the time for today', () => {
    expect(shortDate(unix(new Date(2024, 5, 15, 10, 42)))).toBe('10:42 AM')
  })
  it('shows month and day for earlier this year', () => {
    expect(shortDate(unix(new Date(2024, 0, 21, 9)))).toBe('Jan 21')
  })
  it('shows a short numeric date for earlier years', () => {
    expect(shortDate(unix(new Date(2012, 9, 21, 9)))).toBe('10/21/12')
  })
})

describe('longDate', () => {
  it('has the weekday, date and time', () => {
    expect(longDate(unix(new Date(2013, 6, 1, 15, 5)))).toBe('Mon, Jul 1, 2013 3:05 PM')
  })
})

describe('enclosures', () => {
  it('recognises the kind from the type, or else the extension', () => {
    expect(enclosureKind({ url: 'https://x/a', type: 'audio/mpeg' })).toBe('audio')
    expect(enclosureKind({ url: 'https://x/episode.MP3?token=1', type: '' })).toBe('audio')
    expect(enclosureKind({ url: 'https://x/clip.webm#t=3' })).toBe('video')
    expect(enclosureKind({ url: 'https://x/a', type: 'video/mp4' })).toBe('video')
    expect(enclosureKind({ url: 'https://x/p.jpeg' })).toBe('image')
    expect(enclosureKind({ url: 'https://x/notes.pdf', type: 'application/pdf' })).toBe('file')
  })

  it('labels files by name and media by kind, with the size', () => {
    expect(enclosureLabel({ url: 'https://x/my%20notes.pdf?x=1', length: 2048 })).toBe('my notes.pdf (2.0 KB)')
    expect(enclosureLabel({ url: 'https://x/e.mp3', type: 'audio/mpeg', length: 31457280 })).toBe('Original audio (30.0 MB)')
    expect(enclosureLabel({ url: 'https://x/v', type: 'video/mp4' })).toBe('Original video')
  })
})

describe('formatBytes', () => {
  it('picks a unit', () => {
    expect(formatBytes(0)).toBe('')
    expect(formatBytes(null)).toBe('')
    expect(formatBytes(512)).toBe('512 bytes')
    expect(formatBytes(1536)).toBe('1.5 KB')
    expect(formatBytes(5 * 1024 ** 3)).toBe('5.0 GB')
    expect(formatBytes(3 * 1024 ** 4)).toBe('3072.0 GB')
  })
})

describe('sanitize', () => {
  const html = s => {
    const d = document.createElement('div')
    d.innerHTML = sanitize(s)
    return d
  }

  it('removes scripts, event handlers and styling', () => {
    const d = html('<p style="color:red" class="x" id="y" onclick="evil()">Hi<script>evil()</script><style>p{}</style></p>')
    const p = d.querySelector('p')
    expect(p.textContent).toBe('Hi')
    expect(p.getAttribute('style')).toBeNull()
    expect(p.getAttribute('class')).toBeNull()
    expect(p.getAttribute('id')).toBeNull()
    expect(p.getAttribute('onclick')).toBeNull()
    expect(d.querySelector('script, style')).toBeNull()
  })

  it('removes forms and javascript: links', () => {
    const d = html('<form><input><button>Go</button></form><a href="javascript:evil()">x</a>')
    expect(d.querySelector('form, input, button')).toBeNull()
    expect(d.querySelector('a').getAttribute('href')).toBeNull()
  })

  it('opens links in a new tab and loads images lazily', () => {
    const d = html('<a href="https://example.com">x</a><img src="https://example.com/a.png">')
    expect(d.querySelector('a').getAttribute('target')).toBe('_blank')
    expect(d.querySelector('a').getAttribute('rel')).toBe('noopener noreferrer')
    expect(d.querySelector('img').getAttribute('loading')).toBe('lazy')
  })

  it('copes with nothing', () => {
    expect(sanitize(null)).toBe('')
    expect(sanitize(undefined)).toBe('')
  })
})
