# Keycloak, with the realms inside the image (ADR-0008, deploy slice D4).
#
# In development Aspire imports realms through a **bind mount** of `FieldKit.AppHost/realms`, and
# that is the right mechanism there: edit the file, restart, see the change. It is also the reason
# the published manifest could not deploy — it named
# `D:/Work/Portofolio/FieldKit/FieldKit/FieldKit.AppHost/realms`, an absolute path on one Windows
# machine, as the source of the identity provider's entire configuration.
#
# A container app has no such directory. So for publish, the realms travel in the image.
#
# The realm JSON is *parameterised* rather than duplicated: each origin reads
# `${FIELDKIT_WEB_ORIGIN:http://localhost:3000}`, which Keycloak's import resolves from the
# environment and otherwise defaults to the dev address. One file, both environments — verified
# against Keycloak 26.6, which substitutes `${VAR}` and `${VAR:default}` but silently leaves
# `${env.VAR}` alone (that spelling fails import with "A redirect URI is not a valid URI").
FROM quay.io/keycloak/keycloak:26.6

# Owned by `keycloak:root` — uid 1000, gid 0 — which is what the base image runs as and how every
# other path it owns is grouped.
#
# **No `--chmod`.** The first attempt used `--chmod=440`, reasoning that files only ever read at
# import should not be writable. Docker applies the mode to the *directory it creates* as well, and
# a directory without `x` cannot be opened at all — so import failed with
# `fieldkit-dev-b-realm.json (Permission denied)` on files whose own mode was fine. Left at the
# default, which gives the directory 755 and the files 644.
COPY --chown=keycloak:root realms/*.json /opt/keycloak/data/import/
