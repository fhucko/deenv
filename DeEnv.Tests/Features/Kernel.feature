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
