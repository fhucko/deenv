@milestone-auth
Feature: Seed the first admin (auth bootstrap)
  An app whose schema carries access rules is deny-by-default: with no Admin-role User present,
  no one can ever log in (a role condition fails closed for an anonymous principal, and you cannot
  become a principal without an account). So the FIRST admin must be seeded from OUTSIDE the access
  system — a kernel/server-side operation taking operator-provided credentials, not a gated WS action
  (that would already require being an admin: chicken-and-egg). The seed is invoked directly here (how
  the operator UI collects the credentials is a later slice); it mints an Admin User with a hashed
  password through the store seam, links it into the root's `users` set, and is IDEMPOTENT (a second
  seed never duplicates it). Login sub-slice 1d.

  Scenario: Seeding bootstraps an admin who can then log in
    Given an app with access rules, a User type and a role enum but no users yet
    And no User holds the "Admin" role
    When the operator seeds an admin named "Root" with password "s3cret" and role "Admin"
    Then a User holds the "Admin" role
    And the seeded admin can log in as "Root" with password "s3cret"

  Scenario: Seeding twice does not duplicate the admin
    Given an app with access rules, a User type and a role enum but no users yet
    When the operator seeds an admin named "Root" with password "s3cret" and role "Admin"
    And the operator seeds an admin named "Root" with password "s3cret" and role "Admin"
    Then there is exactly one "Admin" User

  # The seeded admin is linked into the root's `set of User`, so it is a real graph member and survives
  # garbage collection (an unreferenced extent object is swept on the next GC-triggering mutation).
  Scenario: The seeded admin survives garbage collection
    Given an app with access rules, a User type and a role enum but no users yet
    And the operator seeds an admin named "Root" with password "s3cret" and role "Admin"
    When a garbage collection is triggered
    Then the seeded admin can log in as "Root" with password "s3cret"

  # A role value that is not a member of the User.role enum would seed an unusable admin (a role the
  # rules never match), so the seed refuses it loudly rather than writing it.
  Scenario: Seeding with a non-enum role is refused
    Given an app with access rules, a User type and a role enum but no users yet
    When the operator seeds an admin named "Root" with password "s3cret" and role "Superuser"
    Then the seed is refused
    And no User holds the "Superuser" role
