types
    Db
        people set of Person
        tasks set of Task
        settings dict of text by text
        configs dict of Config by text
        slots dict of text by int
    Role enum
        member
        lead
        admin
    Priority enum
        low
        medium
        high
    Person
        name text
        role Role
    Subtask
        title text
        done bool
    Task
        title text
        done bool
        priority Priority
        estimate int
        assignee Person
        subtasks set of Subtask
    Config
        value text
        enabled bool
        owner Person

initialData
    Db 1
        people: [2, 3]
        tasks: [4, 5]
    Person 2
        name: "Ada"
        role: "lead"
    Person 3
        name: "Grace"
        role: "member"
    Task 4
        title: "Design the schema"
        done: true
        priority: "high"
        estimate: 5
        assignee: 2
        subtasks: [6, 7]
    Task 5
        title: "Write the parser"
        priority: "medium"
        estimate: 8
        assignee: 3
    Subtask 6
        title: "Draft types"
        done: true
    Subtask 7
        title: "Round-trip test"
        done: false
