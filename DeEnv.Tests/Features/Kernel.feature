Feature: Kernel host (multi-instance)
  One kernel process hosts multiple instances at once, each on its own port pair
  with its own sovereign data, driven by an instance registry it reads as plain
  kernel-owned data (kernel.json, no interpreter). Milestone 10, first slice:
  the hosting mechanism only — create/list/switch/delete are image Code, later.

  @milestone-10 @single-user
  Scenario: The kernel hosts two instances on distinct ports
    Given a registry of two instances on distinct port pairs
    When the kernel starts
    Then each instance serves its root on its own port

  @milestone-10 @single-user
  Scenario: Each instance keeps its own sovereign data
    Given a registry of two instances on distinct port pairs
    And the kernel has started
    When one instance's data changes
    Then that instance reflects the change
    And the other instance is unchanged

  @milestone-10 @single-user
  Scenario: A registry whose instances would share a store is rejected
    Given a registry of two instances that resolve to the same data file
    When the kernel registry is resolved
    Then it is rejected with a clear kernel-config error

  @milestone-10 @single-user
  Scenario: An instance can render the kernel's list of running instances
    Given a registry whose first instance is a console app that lists the instances
    And the kernel has started
    When I request the console instance's root
    Then the page lists every hosted instance's app and ports

  @milestone-10 @single-user
  Scenario: The kernel creates a new instance while running
    Given a registry of one instance
    And the kernel has started
    When the operator creates an instance from a bool app on a free port pair
    Then the created instance serves its root on its assigned port
    And the kernel now hosts both instances

  @milestone-10 @single-user
  Scenario: A new instance never reuses an orphaned id-dir
    Given a registry of one instance
    And the kernel has started
    And an orphaned instance directory "99" exists
    When the operator creates an instance from a bool app on a free port pair
    Then the created instance's id is past the orphaned directory

  @milestone-10 @single-user
  Scenario: A created instance survives a kernel restart
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance from a bool app on a free port pair
    When the kernel restarts from its persisted registry
    Then the created instance serves its root on its assigned port

  @milestone-10 @single-user
  Scenario: A created instance has its own sovereign store
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance from a bool app on a free port pair
    When the created instance's data changes
    Then the original instance is unchanged

  @milestone-10 @single-user
  Scenario: An already-running instance sees a newly-created one
    Given a registry whose only instance is a console app that lists the instances
    And the kernel has started
    When the operator creates an instance from a bool app on a free port pair
    Then the console instance's page lists the created instance

  @milestone-10 @single-user
  Scenario: The kernel deletes a created instance while running
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance from a bool app on a free port pair
    When the operator deletes the created instance
    Then the deleted instance no longer serves its root
    And the kernel hosts only the original instance
    And the created instance's store directory is gone

  @milestone-10 @single-user
  Scenario: A deleted instance stays gone after a kernel restart
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance from a bool app on a free port pair
    And the operator deletes the created instance
    When the kernel restarts from its persisted registry
    Then the kernel hosts only the original instance

  # Storage is fully id-based and there is no boot-vs-created distinction: delete works on ANY
  # instance, including one that came from the committed registry (a "boot" instance). It stops the
  # instance and removes its id-dir; the committed app SOURCES are git-tracked, the accepted safety
  # net. (The old model refused to delete a boot instance.)
  @milestone-10 @single-user
  Scenario: The kernel deletes a registry instance while running
    Given a registry of two instances on distinct port pairs
    And the kernel has started
    When the operator deletes the second instance by its id
    Then the kernel hosts only the first instance
    And the second instance's store directory is gone

  @milestone-10 @single-user
  Scenario: An already-running instance stops listing a deleted one
    Given a registry whose only instance is a console app that lists the instances
    And the kernel has started
    And the operator creates an instance from a bool app on a free port pair
    When the operator deletes the created instance
    Then the console instance's page no longer lists the created instance

  @milestone-10 @single-user
  Scenario: The kernel clones a created instance, copying its data, while running
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance from a bool app on a free port pair
    And the created instance's data changes
    When the operator clones the created instance onto a free port pair
    Then the clone serves its root on its assigned port
    And the clone's data matches the source
    And the original instance is unchanged
    And the kernel now hosts three instances

  @milestone-10 @single-user
  Scenario: A clone survives a kernel restart
    Given a registry of one instance
    And the kernel has started
    And the operator creates an instance from a bool app on a free port pair
    And the created instance's data changes
    And the operator clones the created instance onto a free port pair
    When the kernel restarts from its persisted registry
    Then the clone serves its root on its assigned port
    And the clone's data matches the source

  # Every instance — boot or created — has a unique id, so a boot instance is individually
  # addressable and cloneable. The old model gave all boots id 0, so cloning "the second boot"
  # would have cloned the first; with unique ids the right boot's data is copied.
  @milestone-10 @single-user
  Scenario: The kernel clones a boot instance by its id, copying the right one's data
    Given a registry of two instances on distinct port pairs
    And the kernel has started
    And the second instance's data changes
    When the operator clones the second instance by its id onto a free port pair
    Then the clone serves its root on its assigned port
    And the clone's data matches the second instance

  @milestone-10 @single-user
  Scenario: The kernel switches an instance to a new port pair
    Given a registry of one instance
    And the kernel has started
    When the operator switches the original instance to a free port pair
    Then the original instance serves its root on the new port
    And the original instance no longer serves its root on the old port

  @milestone-10 @single-user
  Scenario: A switched port binding survives a kernel restart
    Given a registry of one instance
    And the kernel has started
    And the operator switches the original instance to a free port pair
    When the kernel restarts from its persisted registry
    Then the original instance serves its root on the new port

  @milestone-10 @single-user
  Scenario: Switching to a port already in use is rejected
    Given a registry of two instances on distinct port pairs
    And the kernel has started
    When the operator switches the first instance onto the second instance's port
    Then the switch is rejected with a clear kernel-config error
    And both instances still serve their roots on their original ports

  # The design-host (the designer, holding `db.designs`) is seeded over a FRESH store from each registered
  # app's OWN app.app — the designer no longer embeds a duplicated copy of every app. For every registered
  # instance that references a design (carries a `designId`), the kernel reverse-projects that instance's
  # app document into a Design and writes it into the design-host's store AT id == the designId (so
  # kernel.json's references key off the same ids by construction). A registered instance with NO designId
  # contributes no Design. (This is the fresh-store case of the boot sync below — same result.) Driven
  # against a kernel booted from the REAL committed apps + their designIds.
  @milestone-10 @single-user
  Scenario: The design-host is seeded at first boot, each design id pinned to its instance's designId
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    Then the design-host holds a design with id 13 labelled "todo"
    And the design-host holds a design with id 27 labelled "crm"
    And the design-host holds a design with id 60 labelled "designer"
    And the seeded design 13 has a type named "TodoItem"
    And the no-design app contributes no design

  # The design-host's derived library is a BOOT SYNC, not a fresh-only seed: every boot reconciles
  # `db.designs` with the current app files, so adding or editing an app document is reflected on the
  # next restart WITHOUT ever deleting the store. The app file is the source of truth for a file-backed
  # design: an edit to an app's app.app (here, a new prop) is reflected in its design after a restart.
  @milestone-10 @single-user
  Scenario: An edited app document is reflected in its design after restart
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    And the todo app's document gains a new type "Tag"
    When the kernel restarts from its persisted registry
    Then the design-host's design 13 has a type named "Tag"

  # The merge OVERWRITES file-backed designs (the file wins) but PRESERVES designs that have no backing
  # instance — a design created in the UI that no operator has linked to an instance yet. Its id is not
  # among the current designIds, so the boot sync must keep it (never clobber it back to the file set).
  @milestone-10 @single-user
  Scenario: A UI-created design without a backing instance survives a restart
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    And the operator adds a design labelled "scratch" to the design-host
    When the kernel restarts from its persisted registry
    Then the design-host holds a design labelled "scratch"
    And the design-host still holds a design with id 13 labelled "todo"

  # A newly-LINKED app — a registered instance with a designId whose design the store does not yet hold
  # — gets its design on the next boot (the add half of the upsert). Here the design-host store starts
  # WITHOUT todo's design (id 13 removed from db.designs), but todo's instance still references it, so
  # the boot sync reverse-projects todo's app.app into the design at id 13.
  @milestone-10 @single-user
  Scenario: A newly-linked app's design appears after restart
    Given a kernel booted from the committed designer, todo and crm apps plus a no-design app
    And the design-host's design 13 is removed from its store
    When the kernel restarts from its persisted registry
    Then the design-host still holds a design with id 13 labelled "todo"
