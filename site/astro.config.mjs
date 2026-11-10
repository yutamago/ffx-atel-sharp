// SPDX-License-Identifier: MIT
// The site at https://yutamago.github.io/ffx-atel-sharp/ (docs/website.md).
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';

export default defineConfig({
	site: 'https://yutamago.github.io',
	base: '/ffx-atel-sharp',
	integrations: [
		starlight({
			title: 'Atel#',
			// The 404 page is src/content/docs/404.md.
			disable404Route: true,
			plugins: [starlightLinksValidator()],
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/yutamago/ffx-atel-sharp' },
			],
			sidebar: [
				{ label: 'Get started', items: [{ autogenerate: { directory: 'get-started' } }] },
				{ label: 'Guides', items: [{ autogenerate: { directory: 'guides' } }] },
				{ label: 'Concepts', items: [{ autogenerate: { directory: 'concepts' } }] },
				{ label: 'Reference', items: [{ autogenerate: { directory: 'reference' } }] },
				{ label: 'How it works', items: [{ autogenerate: { directory: 'how-it-works' } }] },
				{ label: 'Project', items: [{ autogenerate: { directory: 'project' } }] },
			],
		}),
	],
});
