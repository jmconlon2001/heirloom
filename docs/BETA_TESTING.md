# Beta testing guide

Thank you for helping! You'll look at the app from one of two points of view: **the family admin** (the helpful person who runs it) or **a family member** (who reads the stories). Some of you will also try it as **Grandma Rose**, the storyteller, ideally on an iPad.

The test site runs in **demo mode**: a made-up family is already there, and the sign-in page has one-tap buttons for each person. Nothing you do affects a real family, so try anything.

## Before you start

- Open the test site address you were given, on an **iPad** if you have one (that's what most storytellers use), otherwise any computer or phone.
- Tap **Sign in**, scroll to **Try the demo**, and pick who you are.
- To switch person, tap **Sign out** (top right), then pick another.
- Keep notes as you go: what was confusing, what you'd expect, anything that felt slow or broken. There's a feedback form at the end.

---

## Walkthrough A: the family admin (about 30 minutes)

Sign in as **The family admin**.

1. **The dashboard.** On the home page, find the **Family admin** box. Is it clear whether backups are working? (In the demo, backups may say "not set up yet", and that's expected.)
2. **Invite someone.** People → **Invite someone**. Add yourself, with the Core 26 questions, one a week. Was the form clear? *(Demo sites usually send email to a test mailbox; ask the organiser if you want to see it.)*
3. **A storyteller's page.** People → **Grandma Rose**. Try each of these:
   - **Print a login card**. Would you feel comfortable handing this to a 90-year-old?
   - **Questions just for Grandma Rose**: add one ("What was your first car?").
   - **Reminder emails**: change her day and time.
   - **See the site as Grandma Rose**. Look around, then **Stop viewing as**. Was it obvious you couldn't change anything?
4. **Write for her.** On Rose's page, **Write a story for Grandma Rose**. Answer a question as if she were telling it to you. Then find the story in the Family Library: does it say "Told by Grandma Rose · written by …"?
5. **Questions.** Family admin → **Questions and chapters**. Edit a question's wording, move one up, turn one off.
6. **The family tree.** **Family tree** in the top menu. Add a great-grandparent and connect them (Edit → **Add a connection**). Try **Zoom in** and tapping a person.
7. **Make a book.** Family admin → **Exports** → **A book to print** → **Make it**. When it's done, download the zip and open:
   - `interior.pdf`: does it look like a book you'd be proud of?
   - `cover.pdf`: the spine is in the middle.
   - `preflight.txt`: does the report make sense to you?
   Also try **A family website**: unzip it and open `index.html` straight from the folder.
8. **Email wording.** Family admin → **Email and reminders** → **Email wording** → the reminder. Change a word and press **Preview**.
9. **Activity log.** Family admin → **Activity log**. Can you see what you just did?

Questions for you:
- Was there any point where you didn't know what to do next?
- Is anything an admin would need missing?
- Would you trust this with your family's stories? Why or why not?

---

## Walkthrough B: a family member (about 15 minutes)

Sign in as **Lily (granddaughter)**.

1. **Home.** What do you see first? Is it clear what this place is?
2. **Family Library.** Read **How you met** (Grandma Rose's story about meeting Grandpa Joe).
   - Tap **Love this story**.
   - Write a short note to Grandma.
   - Open **Ask Grandma a follow-up question** and ask something.
3. **Explore.** Try **Timeline**, **Photos** (open one and use **Next photo**), and **Search** (try "tomatoes").
4. **The tree.** **Family tree**. Find Grandpa Joe's page. Can you see the stories about him and the photos he's in?
5. **Write for Grandma.** Lily is set up as Grandma's helper. On the home page, try **Write for Grandma Rose**.
6. **Text size.** Tap **A++** and **High contrast** at the top. Is everything still readable and usable?

Questions for you:
- Did you feel like you were reading your grandmother's stories, or using a website?
- Was anything hard to find?
- Would you come back to read new stories? What would bring you back?

---

## Walkthrough C (optional, on an iPad): Grandma Rose

Sign in as **Grandma Rose**. If you can, hand the iPad to an older relative and watch without helping. Their hesitations are the most valuable feedback we can get.

1. On the home page, tap **Answer this question** (Lily asked about the coat).
2. Type a few words. Watch for **Saved ✓** under the box.
3. Tap **Record my voice**, say a sentence, tap **Stop recording**. Can you play it back?
4. Tap **Add photos** and add a picture from the iPad.
5. Tap **I'm finished with this story**.
6. Back on the home page: what do you think the next step is?

Questions for you (or them):
- Was anything too small, too fast, or confusing?
- Did any words not make sense?
- Did you ever worry you'd lose what you wrote?

---

## Sending feedback

For each thing you noticed, please send:

```
Who I was:        admin / family member / Grandma Rose
Device:           e.g. iPad (Safari), iPhone, Windows laptop (Edge)
Where:            the page, or what I was doing
What happened:
What I expected:
How much it matters to me:  annoying / confusing / stopped me / I loved this
```

A screenshot helps a lot (on iPad: press the top button and volume-up together).

---

## For the organiser: running a beta site

1. Deploy as normal (README → Install), and set in `.env`:
   - `DEMO_MODE=true` (seeds the sample family once, on an empty site, and shows the one-tap demo sign-in);
   - `SMTP_*` pointing at a test mailbox (or Mailpit with `--profile dev`), so invitations and reminders go somewhere you can read them;
   - optionally `FEATURE_PAPER_PACKETS=true` so admins can try paper packets.
2. Demo mode lets anyone with the address sign in as the admin. Put the beta site behind your reverse proxy's basic auth or IP allow-list, and **never** turn demo mode on for a site with real family stories.
3. To reset between rounds of testing, stop the stack, delete `DATA_ROOT`, and start it again.
