import DefaultTheme from 'vitepress/theme'
import { useRouter } from 'vitepress'
import { onMounted } from 'vue'
import './style.css'

let observer

function reveal(root) {
  root.querySelectorAll('figure.shot').forEach((figure) => {
    if (figure.dataset.observed) return
    figure.dataset.observed = '1'
    if (!observer) {
      figure.classList.add('is-visible')
      return
    }
    observer.observe(figure)
  })
}

function ensureLightbox() {
  let overlay = document.querySelector('.shot-lightbox')
  if (overlay) return overlay
  overlay = document.createElement('div')
  overlay.className = 'shot-lightbox'
  overlay.hidden = true
  overlay.innerHTML = '<button class="shot-lightbox-close" type="button" aria-label="关闭">×</button><img alt="">'
  document.body.append(overlay)
  const close = () => {
    overlay.hidden = true
    document.body.style.overflow = ''
  }
  overlay.addEventListener('click', (event) => {
    if (event.target === overlay || event.target.closest('.shot-lightbox-close')) close()
  })
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !overlay.hidden) close()
  })
  document.addEventListener('click', (event) => {
    const image = event.target.closest('figure.shot > img')
    if (!image) return
    const preview = overlay.querySelector('img')
    preview.src = image.currentSrc || image.src
    preview.alt = image.alt || ''
    overlay.hidden = false
    document.body.style.overflow = 'hidden'
  })
  return overlay
}

export default {
  extends: DefaultTheme,
  setup() {
    const router = useRouter()
    onMounted(() => {
      if (typeof window === 'undefined') return
      observer = new IntersectionObserver((entries) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return
          entry.target.classList.add('is-visible')
          observer.unobserve(entry.target)
        })
      }, { rootMargin: '0px 0px -10% 0px' })
      ensureLightbox()
      reveal(document)
      const previous = router.onAfterRouteChanged
      router.onAfterRouteChanged = () => {
        previous?.()
        reveal(document)
      }
    })
  }
}
