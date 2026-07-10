Feature: Assets — the content-addressed blob pool + the `image` scalar
  The per-instance blob pool (docs/plans/assets-design.md): a POST upload edge hashes and stores raw
  bytes content-addressed (`instances/<id>/blobs/<sha256-hex>.<ext>`), a GET serve edge streams them
  back by that name. Bytes live ONLY in the pool; the `image` scalar (a prop's value) carries only the
  hash-name string. Dormant instances only in this slice — no upload ticket/auth.

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
