types
    Db
        users: set of User
    User
        name: text
        todoLists: set of TodoList
    TodoList
        name: text
        items: set of TodoItem
    TodoItem
        text: text
        checked: bool

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
    var title = "Todo list"
    var selectedUser
    var selectedList
    var newUser = getNewUser()
    var newList = getNewList()
    var newItem = getNewItem()

    fn selectUser(user)
        selectedUser = user
        selectedList = null

    fn getNewUser()
        return { name: "", todoLists: [] }

    fn addNewUser()
        db.users.add(newUser)
        newUser = getNewUser()

    fn getNewList()
        return { name: "", items: [] }

    fn addNewList()
        selectedUser.todoLists.add(newList)
        newList = getNewList()

    fn getNewItem()
        return { text: "", checked: false }

    fn addNewItem()
        selectedList.items.add(newItem)
        newItem = getNewItem()

    fn aboutPage()
        return <p class="about">
            "This is the about page"

    fn usersPage()
        return <main class="users">
            <h2>
                "Users"
            <input class="new-user" value={newUser.name}>
            <button class="add-user" onClick={addNewUser}>
                "Add user"
            foreach user in db.users
                <div class="user-row">
                    <span class="user-name" onClick={() => selectUser(user)}>
                        user.name
                    <button class="remove-user" onClick={() => db.users.remove(user)}>
                        "Remove"
            if selectedUser != null
                <h2 class="selected-user">
                    "User "
                    selectedUser.name
                <input class="new-list" value={newList.name}>
                <button class="add-list" onClick={addNewList}>
                    "Add list"
                foreach list in selectedUser.todoLists
                    <div class="list-row">
                        <span class="list-name" onClick={() => selectedList = list}>
                            list.name
                        <button class="remove-list" onClick={() => selectedUser.todoLists.remove(list)}>
                            "Remove"
                if selectedList != null
                    <h2 class="selected-list">
                        "List "
                        selectedList.name
                    <input class="new-item" value={newItem.text}>
                    <button class="add-item" onClick={addNewItem}>
                        "Add item"
                    foreach item in selectedList.items
                        <div class="item-row">
                            <input type="checkbox" class="item-check" checked={item.checked}>
                            if item.checked
                                <span class="item-done">
                                    item.text
                            else
                                <input class="item-text" value={item.text}>
                            <button class="remove-item" onClick={() => selectedList.items.remove(item)}>
                                "Remove"

    fn render()
        var page
        if path == "/about"
            title = "About"
            page = aboutPage
        else
            title = "Todo list"
            page = usersPage
        return <div>
            <h1>
                "Todo list"
            <nav>
                <button class="nav-users" onClick={() => path = "/"}>
                    "Users"
                <button class="nav-about" onClick={() => path = "/about"}>
                    "About"
            page()
