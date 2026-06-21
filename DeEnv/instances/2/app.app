types
    Db
        users set of User
    User
        name text
        todoLists set of TodoList
    TodoList
        name text
        items set of TodoItem
    TodoItem
        text text
        checked bool

initialData
    Db 1
        users: [2]
    User 2
        name: "User 1"
        todoLists: [3]
    TodoList 3
        name: "List 1"
        items: []
ui
    var selectedUser
    var newUser = getNewUser()
    var newList = getNewList()

    fn getNewUser()
        return sys.new(sys.schema("User"))

    fn getNewList()
        return sys.new(sys.schema("TodoList"))

    fn getNewItem()
        return sys.new(sys.schema("TodoItem"))

    fn addNewUser()
        db.users.add(newUser)
        selectedUser = newUser
        newUser = getNewUser()

    fn addNewList()
        selectedUser.todoLists.add(newList)
        newList = getNewList()

    fn chipClass(user)
        if selectedUser == null
            return "user-chip"
        if sys.id(user) == sys.id(selectedUser)
            return "user-chip selected"
        return "user-chip"

    fn userSelector()
        return <section class="user-bar">
            foreach user in db.users
                <button class={chipClass(user)} onClick={() => selectedUser = user}>
                    user.name
            <input class="new-user" value={newUser.name}>
            <button class="add-user" onClick={addNewUser}>
                "Add user"

    fn itemAdder(list)
        var draft = { item: getNewItem() }
        fn add()
            list.items.add(draft.item)
            draft.item = getNewItem()
        fn view()
            return <div class="add-item">
                <input class="new-item" value={draft.item.text}>
                <button class="add-item-btn" onClick={add}>
                    "Add"
        return view

    fn listCard(list)
        return <article class="todo-card">
            <h3 class="list-name">
                list.name
            <ul class="checklist">
                foreach item in list.items
                    <li class="item-row">
                        <Input obj={item} desc={sys.schema("TodoItem", "checked")}>
                        <Input obj={item} desc={sys.schema("TodoItem", "text")} variant="standard">
                        <button class="remove-item" onClick={() => list.items.remove(item)}>
                            "×"
            <itemAdder list={list}>

    fn render()
        return <main class="todo-app">
            <h1>
                "Todos"
            userSelector()
            if selectedUser != null
                <section class="user-lists">
                    <h2 class="selected-user">
                        selectedUser.name
                    <div class="add-list">
                        <input class="new-list" value={newList.name}>
                        <button class="add-list-btn" onClick={addNewList}>
                            "Add list"
                    <div class="cards">
                        foreach list in selectedUser.todoLists
                            listCard(list)
