# Runbook — Facebook Page Token Renewal

**Status:** Agreed · **Last updated:** 2026-07-03

The worker posts to the Facebook page with a **long-lived Page access token**
(docs/05-integrations/facebook.md, ADR-0008). Page tokens die — password changes, security
events, permission changes — and PublishJob then raises
`⚠️ Facebook токенът е невалиден — виж runbook` (at most once per day, from the daily token
health check or a failing post). Site publishing continues; only the Facebook leg stalls.

## 1. Regenerate the long-lived Page token

In the Meta developer console, as an admin of the app and the page:

1. Open [Graph API Explorer](https://developers.facebook.com/tools/explorer/), select the app.
2. *User or Page* → get a **User token** with the `pages_manage_posts` and
   `pages_read_engagement` permissions.
3. Exchange it for a long-lived user token (Graph API Explorer does this via
   *Access Token Tool → Extend*, or `GET /oauth/access_token?grant_type=fb_exchange_token&…`).
4. With the long-lived user token, request `GET /<PAGE-ID>?fields=access_token` — the response
   contains the **long-lived Page token** (does not expire on its own for standard setups).
5. Sanity-check it in the [Access Token Debugger](https://developers.facebook.com/tools/debug/accesstoken/):
   Type = Page, Expires = never (or far future), correct page and app.

## 2. Install the new token on the VPS

From an **elevated** PowerShell:

```powershell
[Environment]::SetEnvironmentVariable('Facebook__AccessToken', '<NEW-PAGE-TOKEN>', 'Machine')
Restart-Service PredelNewsroom
```

(If this install keeps the token in `appsettings.Production.json` instead, edit that file and
restart the service. Never commit the token; docs/06-security.md.)

If the machine variable does not reach the service after `Restart-Service` (the Service
Control Manager caches the environment), reboot the VPS.

## 3. Verify

- Log shows no `Facebook token failed its daily health check` warning after the next publish
  cycle; the once-a-day ⚠️ alert stops.
- Force the proof: approve (or wait for) the next draft — the Facebook leg should post and
  confirm `📘 Публикувано във Facebook: …` in the review chat.
- Drafts stuck `PartiallyPublished` from the outage keep their remaining attempts; ones that
  exhausted attempts were already alerted with "постни ръчно" — post those by hand, the site
  copy is live.
