// Scroll-reveal: elements marked data-reveal fade in the first time they
// enter the viewport. The gate class keeps content fully visible when JS
// never runs (crawlers, reader modes).
export default defineNuxtPlugin((nuxtApp) => {
  document.documentElement.classList.add('has-reveal')

  const io = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          entry.target.classList.add('in')
          io.unobserve(entry.target)
        }
      }
    },
    { rootMargin: '0px 0px -60px 0px', threshold: 0.08 },
  )

  const observe = () => {
    document
      .querySelectorAll('[data-reveal]:not(.in)')
      .forEach((el) => io.observe(el))
  }

  nuxtApp.hook('page:finish', observe)
  nuxtApp.hook('app:mounted', observe)
})
