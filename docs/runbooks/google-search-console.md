# Runbook — Google Search Console & Google News/Discover

**Owner action. Prerequisites:** the site deployed on https://predelnews.com; access to the
Google account that will own the property; (optional, better) access to the domain's DNS.

## 1. Verify ownership
Preferred: **Domain property** — GSC → Add property → Domain → `predelnews.com` → add the
shown TXT record in DNS. Covers all subdomains + both protocols. Keep the record forever.
Fallback: **URL-prefix property** `https://predelnews.com/` with the **meta-tag method** —
copy the `content` token from GSC and set it in production config:
`PredelNews:Seo:GoogleSiteVerification` (appsettings.Production.json or the
`PredelNews__Seo__GoogleSiteVerification` environment variable), restart the site, click
Verify. The tag renders site-wide automatically. Keep at least one backup method active.

## 2. Submit the sitemaps
GSC → Sitemaps → submit `https://predelnews.com/sitemap.xml` and
`https://predelnews.com/news-sitemap.xml`. Both are also declared in robots.txt.
There is NO ping endpoint anymore (Google removed it in 2023) and NO Publisher Center
submission anymore (removed 2024; Google News publication pages are auto-generated since
March 2025). Inclusion in News/Discover is automatic and policy-based — the on-site work
(news sitemap, NewsArticle JSON-LD, max-image-preview:large, ≥1200 px images) is everything
we control.

## 3. What to watch (weekly, per the promotion strategy KPIs)
- **Performance → Search results**: clicks/impressions, query mix (expect local queries).
- **Performance → Discover**: appears only after the site crosses Discover's impression
  threshold — typically weeks after indexing; its absence early on is normal.
- **Indexing → Pages**: watch for "Crawled — not indexed" spikes and sitemap errors.
- **Sitemaps**: news-sitemap "Success" status; it is normal for it to report few URLs
  (only the last 48 h of articles are listed).

## 4. Troubleshooting
- Verification fails via meta tag → confirm the token renders in View Source on the live
  site (config deployed? site restarted?), and no redirect intercepts the homepage.
- news-sitemap 404 → the site build predates the SEO pack; deploy the current main.
- Timestamps look shifted → stored dates are treated as UTC by design (decision 2026-07-18);
  editor-entered publishDate values typed as local time will show a 2–3 h offset — enter
  UTC times in the backoffice for manual articles, or accept the small skew.

## 5. Open follow-up (one-time, after first live traffic)
- Paste a live article URL into https://validator.schema.org/ (and Google's Rich Results
  Test) and confirm the NewsArticle JSON-LD reports 0 errors.
- Confirm the sub-1200 px cover-image warning fires: after the first automated publish whose
  source image is narrower than 1200 px, check the site log for the "below the 1200px Google
  Discover minimum" warning (Predel-News `NewsroomPublishingService`).
