Feature: Assets — the content-addressed blob pool + the `image` scalar
  The per-instance blob pool (docs/plans/assets-design.md): a POST upload edge hashes and stores raw
  bytes content-addressed (`instances/<id>/blobs/<sha256-hex>.<ext>`), a GET serve edge streams them
  back by that name. Bytes live ONLY in the pool; the `image` scalar (a prop's value) carries only the
  hash-name string. A DORMANT instance's upload edge stays open with no session cookie; a RULED instance
  (any access rule declared) requires the AMBIENT SESSION COOKIE for a real principal (§2, slice 2b — the
  exact /session mechanism, superseding the slice-2 short-lived ticket). A same-host Origin check is a
  CSRF belt-and-braces second lock on top of the cookie.

  Background:
    Given the assets app is running

  @assets
  Scenario: An uploaded image round-trips through the pool by its content hash
    When I upload 40 random bytes as "image/png"
    Then the uploaded name matches the pool pattern for extension "png"
    When I GET the uploaded blob
    Then the asset response status is 200
    And the asset response body is the uploaded bytes
    And the asset response content type is "image/png"
    And the asset response is cacheable, immutable, and marked nosniff

  @assets
  Scenario: Identical bytes uploaded twice dedup to one pool file
    When I upload 40 random bytes as "image/png"
    And I upload the same bytes again as "image/png"
    Then both uploads returned the same name

  @assets
  Scenario: A GET whose name is not a bare content hash is refused without touching files outside the pool
    When I GET the asset name "../../secret.txt"
    Then the asset response status is 404

  @assets
  Scenario: A GET of a well-formed but absent hash returns 404
    When I GET a well-formed but absent blob name
    Then the asset response status is 404

  @assets
  Scenario: An image value persists across a store reload
    When I upload 40 random bytes as "image/png"
    And I set the Db "photo" field to the uploaded name
    Then the store eventually has a "Db" whose "photo" field is the uploaded name
    When the store is opened again on the same data file
    Then the store opens successfully
    And the reopened store's "Db" "photo" field is the uploaded name

  @assets
  Scenario: An upload past the 10 MB cap is rejected and leaves no blob file
    When I upload 11000000 random bytes as "image/png"
    Then the asset response status is 413
    And no temp file remains in the pool

  @assets
  Scenario: The generic form for an image prop shows the upload control and a thumbnail after upload
    Given a browser is open on the assets app
    When I open "/"
    Then the page shows "input[type=file]"
    When I upload a real image file to the photo field
    Then the page shows "img.image-thumb"

  # ── the upload auth: the ambient session cookie (assets slice 2b, §2) ───────────────

  @assets
  Scenario: Upload on a dormant instance succeeds with no cookie sent at all (slice 1 regression guard)
    When I upload 40 random bytes as "image/png"
    Then the asset response status is 200

  @assets
  Scenario: An upload on a ruled instance with no cookie is rejected and leaves no blob file
    Given the ruled assets app is running
    When I upload 40 random bytes as "image/png" with no cookie
    Then the asset response status is 401
    And no temp file remains in the pool

  @assets
  Scenario: A logged-in session on a ruled instance uploads with its session cookie and the upload succeeds
    Given the ruled assets app is running
    And the seeded user has the password "secret123"
    When the session logs in as "Alice" with password "secret123" via the session endpoint
    And I upload 40 random bytes as "image/png" with the session's cookie
    Then the asset response status is 200
    When I GET the uploaded blob
    Then the asset response status is 200
    And the asset response body is the uploaded bytes

  @assets
  Scenario: A garbage cookie is rejected on a ruled instance and leaves no blob file
    Given the ruled assets app is running
    When I upload 40 random bytes as "image/png" with a garbage cookie
    Then the asset response status is 401
    And no temp file remains in the pool

  @assets
  Scenario: A cross-site Origin on the upload POST is rejected before any auth or disk work
    When I upload 40 random bytes as "image/png" with the Origin header "http://evil.example"
    Then the asset response status is 403
    And no temp file remains in the pool

  # ── the custom-app composition proof (assets slice 3, §4) ───────────────────

  @assets
  Scenario: A fully-custom render composes the public ImageInput component and sys.assetUrl
    Given a browser is open on the custom-photo assets app
    When I open "/"
    Then the page shows "input[type=file]"
    And the page shows "div.custom-photo-empty"
    When I upload a real image file to the photo field
    Then the page shows "img.custom-photo"
    And the custom photo thumbnail src matches the pool pattern for extension "png"
    When I reload the page
    Then the page shows "img.custom-photo"
    And the custom photo thumbnail src matches the pool pattern for extension "png"
