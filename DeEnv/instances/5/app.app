types
    Db
        milestones set of Milestone
    Milestone
        name text
        status Status
        notes text multiline
        slices set of Slice
    Slice
        name text
        status Status
        notes text multiline
    Status enum
        planned
        active
        done
        deferred

initialData
    Db 1
        milestones: [2, 3, 4]
    Milestone 2
        name: "M11 - reactive components + public library"
        status: "done"
        slices: []
    Milestone 3
        name: "Gate #1 - non-destructive apply"
        status: "done"
        slices: []
    Milestone 4
        name: "Gate #3 - dogfood a real app"
        status: "active"
        slices: [5, 6]
    Slice 5
        name: "Dev tracker v1 (this app)"
        status: "active"
    Slice 6
        name: "Evolve the schema by using it"
        status: "planned"
