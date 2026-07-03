-- 0009_publish_ref: globally-unique idempotency reference for publishing.
-- Draft row IDs are identity values that restart when a database is rebuilt, but the
-- publishing endpoint's idempotency ledger (pn_NewsroomPublish on the site) outlives such
-- resets — an id-based externalRef then collides with a ledger entry from a previous life
-- and the endpoint silently returns the OLD article (hit live 2026-07-03). A GUID minted at
-- row creation survives every reset. Single batch, no GO.

ALTER TABLE dbo.nw_Draft ADD
    PublishRef uniqueidentifier NOT NULL
        CONSTRAINT DF_nw_Draft_PublishRef DEFAULT NEWID();
