# deenv: what matters most

A simpler start, in plain words; the terms of the earlier maps are left out on purpose. The goals come first, then two views; everything else is an implementation detail. F*n* is fragment *n* in [#21](https://github.com/fhucko/deenv/issues/21).

## 0. Why deenv

Thousands of developers implement the same things again and again, with small differences, and need many tools for development and DevOps. (F76) The experience of building software has stood still for about twenty years, and deenv wants to move it forward. (F85)

deenv speeds up development (F80):

- the developer does not have to install tools;
- the developer does not have to write boilerplate;
- the developer does not have to write what almost every project needs: the core and the system modules bring the general things, such as security and data transfer, and libraries bring the rest. (F86)

AI writes code fast but not reliably, and less code in what people build for the same functionality is the answer. An error here means anything people dispute in code review, suboptimal code and architecture included. (F78, F79)

## 1. The user (uses an app built in deenv)

- **Data, screens and permissions work as in today's deenv.** (F22, F65)
- **The user never notices that the app was updated.** (F67, F68)
- **The app is fast.** (F22)

## 2. The developer and DevOps (build and run an app in deenv)

- **Building an app takes nothing to install,** and the developer does not have to deal with how browser and server talk. (F74)
- **Data, screens and permissions work as in today's deenv.** (F22, F65)
- **The developer builds an app from own code and libraries.** Calendars, shops and the like come as libraries, ideally in many variants, so a calendar that someone dislikes is no reason to drop deenv. Libraries come after the first version. (F86; agreed in review, 2026-11-03)
- **An app takes little code and few decisions that someone could dispute in review.** (F78, F79)
- **Running an app happens in the same environment,** next to building it. (F17, F18)
- **A new version goes out while the app keeps running for its users.** (F67, F68)
- **Everything is fast,** tests included. (F22, F66)

## 3. Who the first version is for

Small apps with a few users, in the spirit of WordPress (F81): for example a presentation and booking website for a masseur, or a small e-shop. The first version has to let a developer build:

- public pages that anyone opens without signing in, in the look the developer wants;
- booking: free slots, a booking form, a confirmation;
- products, a cart and orders;
- an admin part where the owner manages bookings, orders, products and texts;
- images.

The first version needs only a database and a website: no online payment and no emails. (F82)

Several people may use the app at once. The first version protects only what would break a booking or an order: two customers do not get the same slot, the second learns it is taken when sending the form, and the e-shop does not sell the last piece twice. The second version adds live changes: one user's edit shows on another user's open screen. (agreed in review, 2026-11-01)

More apps of the same kind (agreed in review, 2026-11-01):

- a hairdresser, a physiotherapist or a tutor: booking, as for the masseur;
- a small guesthouse: rooms, occupancy, a stay request;
- a café or a restaurant: a menu, opening hours, a table booking;
- a club: members, events, sign-ups;
- a craftsman: an inquiry form, quotes, a list of jobs;
- an event or a workshop: registration and a list of participants;
- an internal tool of a small firm: orders, stock, a simple CRM.

## 4. Everything else is an implementation detail

deenv has two worlds with a thick line between them (F84, F87):

- **The core and the system modules** do each thing ideally one way. They are written once and well tested. They may be complex, though simple is welcome as well. A programming language, React and EF work the same way: they are built once, and many people then use them to simplify their work. Examples of system modules are defining a web app, client-server communication without server code, and permissions, as in today's deenv.
- **Apps and libraries** do things a thousand ways, simply. They have to stay clean: little code and few decisions that someone could dispute in review. (F78, F79)

What happens on the core side is our choice: it can be simple in the first version, even in C#, and improve in later versions. It is judged by what the two views notice. (F73, F71, F2)

System modules need an interface. In the first version only we write them, and a later version makes the interface public, so that an ecosystem of modules can grow. deenv does not fix the list. (F22, F66; agreed in review, 2026-11-03) More of deenv can be modular as well, even the whole system and its core. (F83)
