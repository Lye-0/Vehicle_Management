export function printDocument(filename: string) {
  const originalTitle = document.title
  const cleanup = () => {
    document.title = originalTitle
    window.removeEventListener('afterprint', cleanup)
  }
  document.title = filename
  window.addEventListener('afterprint', cleanup, { once: true })
  window.print()
  window.setTimeout(cleanup, 10_000)
}
