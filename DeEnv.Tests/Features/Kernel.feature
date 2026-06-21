Feature: Kernel host (multi-instance, path-addressed)
  One kernel process hosts multiple instances at once behind ONE shared app port + ONE shared
  asset port, each instance addressed by PATH (/apps/<name>) with its own sovereign data, driven
  by an instance registry it reads as plain kernel-owned data (kernel.json, no interpreter). The
  hosting mechanism only — create/list/delete are image Code. Milestone 10.

  @milestone-10 @single-user
  Scenario: The kernel serves each instance at its mount path
    Given a registry of two named instances
    When the kernel starts
    Then each instance serves its root at its mount path on the app port

  # Mount-awareness: the instance is base-unaware internally (root-relative `path` + links), and the
  # kernel applies the mount at the edges — so a generic-UI page's breadcrumbs + nested links carry
  # the /apps/<name> prefix, and the injected base is /apps/<name>.
  @milestone-10 @single-user
  Scenario: A path-mounted instance serves mount-aware links and base
    Given a registry of one generic-UI instance named "site"
    And the kernel has started
    When I request the "site" instance at its path
    Then the page's links carry the "/apps/site" prefix
    And the page's injected base is "/apps/site"

  @milestone-10 @single-user
  Scenario: Each instance keeps its own sovereign data
    Given a registry of two named instances
    And the kernel has started
    When one instance's data changes
    Then that instance reflects the change
    And the other instance is unchanged

  @milestone-10 @single-user
  Scenario: A registry whose instances would share a store is rejected
    Given a registry of two instances that resolve to the same data file
    When the kernel registry is resolved
    Then it is rejected with a clear kernel-config error

  # Two instances cannot share a mount name — they would collide on /apps/<name> (the second would be
  # unreachable). The kernel rejects it upfront, the path-addressing analogue of the shared-store guard.
  @milestone-10 @single-user
  Scenario: A registry whose instances share a mount name is rejected
    Given a registry of two instances that share a mount name
    When the kernel registry is resolved
    Then it is rejected with a clear kernel-config error mentioning the name

  @milestone-10 @single-user
  Scenario: An instance can render the kernel's list of running instances
    Given a registry whose first instance is a console app that lists the instances
    And the kernel has started
    When I request the console instance at its path
    Then the page lists every hosted instance's name and path

  @milestone-10 @single-user
  Scenario: The kernel creates a new named instance while running
    Given a registry of one instance
    And the kernel has started
    When the operator creates an instance named "fresh" from a bool app
    Then the created instance serves its root at "/apps/fresh"
    And the kernel now hosts both instances

  @milestone-10 @single-user
  Scenario: A new instance never reuses an orphaned id-dir
    Given a registry of one instance
    And the kernel has started
    And an orphaned instance directory "99" exists
    When the operator creates an instance named "fresh" from a bool app
    Then the created instance's id is past the orphaned directory

  @milestone-10 @single-user
  Scenario: A created instance survives a kernel restart
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance named "fresh" from a bool app
    When the kernel restarts from its persisted registry
    Then the created instance serves its root at "/apps/fresh"

  @milestone-10 @single-user
  Scenario: A created instance has its own sovereign store
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance named "fresh" from a bool app
    When the created instance's data changes
    Then the original instance is unchanged

  @milestone-10 @single-user
  Scenario: An already-running instance sees a newly-created one
    Given a registry whose only instance is a console app that lists the instances
    And the kernel has started
    When the operator creates an instance named "fresh" from a bool app
    Then the console instance's page lists the created instance

  @milestone-10 @single-user
  Scenario: The kernel deletes a created instance while running
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance named "fresh" from a bool app
    When the operator deletes the created instance
    Then the deleted instance no longer serves its root
    And the kernel hosts only the original instance
    And the created instance's store directory is gone

  @milestone-10 @single-user
  Scenario: A deleted instance stays gone after a kernel restart
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance named "fresh" from a bool app
    And the operator deletes the created instance
    When the kernel restarts from its persisted registry
    Then the kernel hosts only the original instance

  # Storage is fully id-based and there is no boot-vs-created distinction: delete works on ANY
  # instance, including one that came from the committed registry (a "boot" instance). It stops
  # routing the instance and removes its id-dir; the committed app SOURCES are git-tracked.
  @milestone-10 @single-user
  Scenario: The kernel deletes a registry instance while running
    Given a registry of two named instances
    And the kernel has started
    When the operator deletes the second instance by its id
    Then the kernel hosts only the first instance
    And the second instance's store directory is gone

  @milestone-10 @single-user
  Scenario: An already-running instance stops listing a deleted one
    Given a registry whose only instance is a console app that lists the instances
    And the kernel has started
    And the operator creates an instance named "fresh" from a bool app
    When the operator deletes the created instance
    Then the console instance's page no longer lists the created instance

  @milestone-10 @single-user
  Scenario: The kernel clones a created instance, copying its data, while running
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance named "fresh" from a bool app
    And the created instance's data changes
    When the operator clones the created instance
    Then the clone serves its root at its own path
    And the clone's data matches the source
    And the original instance is unchanged
    And the kernel now hosts three instances

  @milestone-10 @single-user
  Scenario: A clone survives a kernel restart
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance named "fresh" from a bool app
    And the created instance's data changes
    And the operator clones the created instance
    When the kernel restarts from its persisted registry
    Then the clone serves its root at its own path
    And the clone's data matches the source

  # Every instance — boot or created — has a unique id, so a boot instance is individually
  # addressable and cloneable. The right boot's data is copied (the id resolves the source).
  @milestone-10 @single-user
  Scenario: The kernel clones a boot instance by its id, copying the right one's data
    Given a registry of two named instances
    And the kernel has started
    And the second instance's data changes
    When the operator clones the second instance by its id
    Then the clone serves its root at its own path
    And the clone's data matches the second instance

  # Rename is now a RE-MOUNT: the instance's path becomes /apps/<new>. The old path stops serving,
  # the new path serves, and the rebuilt instance's links carry the new prefix.
  @milestone-10 @single-user
  Scenario: The kernel renames an instance, re-mounting it at the new path
    Given a registry of one instance
    And the kernel has started
    When the operator renames the original instance to "renamed"
    Then the original instance serves its root at "/apps/renamed"
    And the original instance no longer serves its root at its old path

  @milestone-10 @single-user
  Scenario: A rename survives a kernel restart
    Given a registry of one instance
    And the kernel has started
    And the operator renames the original instance to "renamed"
    When the kernel restarts from its persisted registry
    Then the original instance serves its root at "/apps/renamed"

  @milestone-10 @single-user
  Scenario: Renaming onto a name already in use is rejected
    Given a registry of two named instances
    And the kernel has started
    When the operator renames the first instance onto the second instance's name
    Then the rename is rejected with a clear kernel-config error
    And both instances still serve their roots at their own paths

  # ── the shared asset port (per-app routing) ──────────────────────────────────
  # The asset port serves each instance's /ws + /js under its own /apps/<name> mount (the foundation
  # for per-app assets — today every instance serves the same bundle). A path-mounted instance's
  # WebSocket connects + a save persists; its /js loads.
  @milestone-10 @single-user
  Scenario: The shared asset port serves a path-mounted instance's bundle
    Given a registry of one instance
    And the kernel has started
    When I request "/js" under the original instance's mount on the asset port
    Then the asset response is the client bundle

  @milestone-10 @single-user
  Scenario: A path-mounted instance's WebSocket connects and a save persists
    Given a registry of one instance
    And the kernel has started
    When I open the original instance in a browser at its path
    And I toggle its checkbox
    Then the original instance's store eventually has the bool set
    And the page is fully ready

  # ── domain capability (no Host routing — nginx's job) ─────────────────────────
  # A request carrying X-Forwarded-Prefix: "" is served at the domain ROOT: the page's links are
  # root-relative (no /apps/<name> prefix) and the injected base is "/". This is the primitive that
  # lets nginx proxy <name>.<domain>/ → the kernel and serve the app at the domain root. No Host
  # routing is built in the kernel; only the prefix override.
  @milestone-10 @single-user
  Scenario: An X-Forwarded-Prefix override serves the app at a domain root
    Given a registry of one generic-UI instance named "site"
    And the kernel has started
    When I request the "site" instance at its path with X-Forwarded-Prefix ""
    Then the page's links are root-relative
    And the page's injected base is "/"

  # The design-host (the designer, holding `db.designs`) is seeded over a FRESH store from each
  # registered app's OWN app.app. For every registered instance that references a design (carries a
  # `designId`), the kernel reverse-projects that instance's app document into a Design at id ==
  # the designId. A registered instance with NO designId contributes no Design.
  @milestone-10 @single-user
  Scenario: The design-host is seeded at first boot, each design id pinned to its instance's designId
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    Then the design-host holds a design with id 13 labelled "todo"
    And the design-host holds a design with id 27 labelled "crm"
    And the design-host holds a design with id 60 labelled "designer"
    And the seeded design 13 has a type named "TodoItem"
    And the no-design app contributes no design

  @milestone-10 @single-user
  Scenario: An edited app document is reflected in its design after restart
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    And the todo app's document gains a new type "Tag"
    When the kernel restarts from its persisted registry
    Then the design-host's design 13 has a type named "Tag"

  @milestone-10 @single-user
  Scenario: A UI-created design without a backing instance survives a restart
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    And the operator adds a design labelled "scratch" to the design-host
    When the kernel restarts from its persisted registry
    Then the design-host holds a design labelled "scratch"
    And the design-host still holds a design with id 13 labelled "todo"

  @milestone-10 @single-user
  Scenario: A newly-linked app's design appears after restart
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    And the design-host's design 13 is removed from its store
    When the kernel restarts from its persisted registry
    Then the design-host still holds a design with id 13 labelled "todo"
