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

  @milestone-10 @single-user
  Scenario: An already-running instance stops listing a deleted one
    Given a registry whose only instance is a console app that lists the instances
    And the kernel has started
    And the operator creates an instance from a bool app on a free port pair
    When the operator deletes the created instance
    Then the console instance's page no longer lists the created instance

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
