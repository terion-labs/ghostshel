# GhostSHELL website

The marketing site for GhostSHELL, served from GitHub Pages.

Nuxt with a static build target. No server; `pnpm generate` writes plain
files to `.output/public`.

## Develop

```sh
pnpm install
pnpm dev
```

## Build

```sh
pnpm generate
```

The site is built for the repository Pages path `/ghostshel/`. Publishing to
a custom domain means setting `NUXT_APP_BASE_URL=/` for the build.

## Deploy

`.github/workflows/website.yml` builds and deploys on every push to `main`
that touches `website/`. It is separate from the application workflows: the
repository gate runs on version tags and the database-viewer workflow is
path-filtered to application sources, so website changes trigger neither.

## Screenshots

Images in `public/shots/` are WebP copies of the design-QA captures in
`artifacts/design-qa/website/`, downscaled to 1760px wide:

```sh
sips -Z 1760 shot.png && cwebp -q 82 -m 6 shot.png -o shot.webp
```
