using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Demo;

/// <summary>
/// DEMO_MODE only: a believable sample family for beta testers. Grandma Rose (storyteller, several
/// finished stories with photos), Grandpa Joe's memories written by Lily, Lily (granddaughter,
/// family member), a small tree, hearts, notes and a follow-up question. Runs once, on an empty site.
/// </summary>
public sealed class DemoSeeder(
    AppDbContext db, UserManager<AppUser> users, MemoryService memories, AttachmentService attachments,
    PeopleService people, SharingService sharing, ConversationService conversation, AppOptions options,
    TimeProvider clock, ILogger<DemoSeeder> log)
{
    public const string RoseEmail = "rose@demo.family";
    public const string LilyEmail = "lily@demo.family";
    public const string AnnEmail = "ann@demo.family";

    public async Task SeedAsync()
    {
        if (await db.Users.AnyAsync(u => u.Email == RoseEmail)) return;
        log.LogWarning("DEMO_MODE: creating the sample family");
        var now = clock.GetUtcNow().UtcDateTime;
        var full = await db.PromptSets.SingleAsync(s => s.Kind == PromptSetKind.Core);

        async Task<AppUser> User(string name, string email, Guid? set, params string[] roles)
        {
            var u = new AppUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = name, CreatedAt = now.AddDays(-60), PromptSetId = set, Pacing = Pacing.AllAtOnce, PacingStartedAt = set is null ? null : now.AddDays(-60) };
            await users.CreateAsync(u);
            await users.AddToRolesAsync(u, roles);
            return u;
        }
        var rose = await User("Grandma Rose", RoseEmail, full.Id, Roles.Storyteller, Roles.Family);
        var ann = await User("Ann Smith", AnnEmail, full.Id, Roles.Storyteller, Roles.Family);
        var lily = await User("Lily Smith", LilyEmail, null, Roles.Family);
        db.StorytellerHelpers.Add(new StorytellerHelper { StorytellerId = rose.Id, HelperId = lily.Id, CreatedAt = now });
        await db.SaveChangesAsync();

        // The tree.
        Person P(string given, string surname, Sex sex, int born, int? died = null, string? nick = null, Guid? user = null, string? birthSurname = null, string? place = null) =>
            new() { Id = Guid.CreateVersion7(), GivenNames = given, Surname = surname, BirthSurname = birthSurname, Nickname = nick, Sex = sex, BirthYear = born, BirthPlace = place,
                    IsLiving = died is null, DeathYear = died, LinkedUserId = user, CreatedAt = now, UpdatedAt = now };
        var walter = P("Walter", "Miller", Sex.Male, 1902, 1971, place: "Akron, Ohio");
        var edna = P("Edna", "Miller", Sex.Female, 1905, 1988, birthSurname: "Hughes");
        var roseP = P("Rose", "Smith", Sex.Female, 1931, nick: "Grandma Rose", user: rose.Id, birthSurname: "Miller", place: "Akron, Ohio");
        var joe = P("Joseph", "Smith", Sex.Male, 1928, 2004, nick: "Grandpa Joe");
        var annP = P("Ann", "Smith", Sex.Female, 1956, user: ann.Id);
        var david = P("David", "Smith", Sex.Male, 1959);
        var lilyP = P("Lily", "Smith", Sex.Female, 1994, user: lily.Id);
        joe.Bio = "Fixed every tractor in Summit County, and grew the best tomatoes at the fair three years running.";
        db.People.AddRange(walter, edna, roseP, joe, annP, david, lilyP);
        await db.SaveChangesAsync();
        await people.AddPartnerAsync(walter.Id, edna.Id, PartnershipKind.Married, 1926, null);
        await people.AddPartnerAsync(roseP.Id, joe.Id, PartnershipKind.Married, 1952, null);
        foreach (var (child, parents) in new[] { (roseP, new[] { walter, edna }), (annP, new[] { roseP, joe }), (david, new[] { roseP, joe }), (lilyP, new[] { david }) })
            foreach (var parent in parents) await people.AddParentAsync(child.Id, parent.Id, ParentKind.Biological);

        // Stories.
        var photos = Path.Combine(options.UploadsDir, "demo");
        Directory.CreateDirectory(photos);
        async Task<Memory> Story(AppUser teller, string key, string body, FuzzyDate when, string? place, Visibility vis, Guid[] tagged,
            (string Caption, MagickColor Sky, MagickColor Ground, Guid[] People)[] pictures, Guid? writtenBy = null, DateTime? finished = null)
        {
            var prompt = await db.Prompts.SingleAsync(p => p.Key == key);
            var m = await memories.GetOrCreateForPromptAsync(teller.Id, prompt, writtenBy ?? teller.Id);
            await memories.SaveAsync(m.Id, new MemoryEdit(null, body, when, place), writtenBy ?? teller.Id, VersionKind.Save);
            foreach (var pic in pictures)
            {
                var file = Path.Combine(photos, $"{Guid.NewGuid():N}.jpg");
                Picture(file, pic.Sky, pic.Ground);
                await using var stream = File.OpenRead(file);
                var (a, _) = await attachments.AddFromStreamAsync(m.Id, Path.GetFileName(file), stream, teller.Id);
                if (a is null) continue;
                await attachments.SetCaptionAsync(m.Id, a.Id, pic.Caption);
                foreach (var pid in pic.People) await people.TagPhotoAsync(a.Id, pid, true);
            }
            await people.SetMemoryPeopleAsync(m.Id, tagged);
            await sharing.SetVisibilityAsync(m.Id, vis, []);
            await memories.SetStatusAsync(m.Id, MemoryStatus.Complete, writtenBy ?? teller.Id);
            if (finished is { } f) await db.Memories.Where(x => x.Id == m.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, f));
            return m;
        }

        var born = await Story(rose, "roots-born",
            "I was born at home on June 12, 1931, in the front bedroom of our farmhouse outside Akron. The doctor came out from town in a Model A, and my father always said he got there just in time to take the credit.\n\nMy mother named me after the rose bush by the porch. It was the only thing on the farm she planted just because it was pretty.",
            new FuzzyDate(1931, 6, 12, false, null, null), "Akron, Ohio", Visibility.AllFamily, [roseP.Id, walter.Id, edna.Id],
            [("The farmhouse, about 1935", new MagickColor("#9fc3df"), new MagickColor("#7a9a52"), [])], finished: now.AddDays(-40));
        var meeting = await Story(rose, "love-meeting",
            "It was the Summit County Fair, the summer of 1950. Joe had entered his tomatoes, and he won the blue ribbon. He walked right over and pinned it on my coat. I told him that was the boldest thing I'd ever seen, and he said, \"Then you'd better let me buy you a lemonade.\"\n\nWe were married two years later. He grew tomatoes every summer for the rest of his life.",
            new FuzzyDate(1950, 8, null, false, null, null), "Summit County Fair", Visibility.AllFamily, [joe.Id, roseP.Id],
            [("Joe and me at the fair, 1950", new MagickColor("#f2d5a0"), new MagickColor("#c2a272"), [joe.Id, roseP.Id]),
             ("Our wedding day, June 1952", new MagickColor("#e9e4dc"), new MagickColor("#b9ad9b"), [joe.Id, roseP.Id])], finished: now.AddDays(-9));
        await Story(rose, "food-recipe",
            "Mother's apple butter. Two pecks of apples, a gallon of cider, three cups of sugar, cinnamon and cloves. You stir it in the copper kettle all afternoon so it doesn't scorch, and everybody takes a turn.\n\nThe secret is patience. And a little more cinnamon than you think.",
            new FuzzyDate(null, null, null, false, 1940, null), null, Visibility.AllFamily, [edna.Id],
            [("The recipe card in Mother's handwriting", new MagickColor("#efe6cf"), new MagickColor("#d8c9a3"), [edna.Id])], finished: now.AddDays(-3));
        await Story(rose, "people-miss",
            "I miss my sister Ruth. We shared a bed until I was fourteen and talked every night until Mother knocked on the wall.",
            default, null, Visibility.Private, [], [], finished: now.AddDays(-2));
        // Lily wrote one down for Grandma (proxy entry).
        await Story(rose, "growing-games",
            "We played kick-the-can until it was too dark to see the can. My best friend was Dorothy Bauer from the next farm over. We built a fort in the hayloft and made her little brother the guard.",
            new FuzzyDate(1939, null, null, true, null, null), null, Visibility.AllFamily, [roseP.Id], [], writtenBy: lily.Id, finished: now.AddDays(-1));
        await Story(ann, "family-funny",
            "When David was four he decided the dog needed a haircut. We came home to find the dog, the scissors, and a very proud little boy.",
            new FuzzyDate(1963, null, null, true, null, null), null, Visibility.AllFamily, [david.Id], [], finished: now.AddDays(-5));

        // A little life on the stories.
        await conversation.ToggleHeartAsync(meeting.Id, lily.Id);
        await conversation.ToggleHeartAsync(meeting.Id, ann.Id);
        await conversation.ToggleHeartAsync(born.Id, lily.Id);
        await conversation.AddCommentAsync(meeting.Id, lily.Id, "Grandma, I never knew Grandpa pinned the ribbon on you! ♥");
        var meetingFull = await db.Memories.SingleAsync(m => m.Id == meeting.Id);
        await conversation.AskFollowUpAsync(meetingFull, lily.Id, "What color was your coat that day?");
        // One story in progress, so the writing page has something to show.
        var draftPrompt = await db.Prompts.SingleAsync(p => p.Key == "roots-home");
        var draft = await memories.GetOrCreateForPromptAsync(rose.Id, draftPrompt, rose.Id);
        await memories.SaveAsync(draft.Id, new MemoryEdit(null, "The kitchen always smelled of woodsmoke and bread.", default, null), rose.Id, VersionKind.Autosave);
        try { Directory.Delete(photos, true); } catch (IOException) { }
    }

    /// <summary>A soft, painterly stand-in for an old photo: sky, a field, a house shape and a sun. No text (fonts vary between systems).</summary>
    private static void Picture(string path, MagickColor sky, MagickColor ground)
    {
        using var image = new MagickImage(sky, 1200, 900);
        new Drawables()
            .FillColor(ground).Rectangle(0, 560, 1200, 900)
            .FillColor(new MagickColor("#8b3a2b")).Polygon(new PointD(420, 420), new PointD(600, 300), new PointD(780, 420))
            .FillColor(new MagickColor("#f4efe4")).Rectangle(450, 420, 750, 600)
            .FillColor(new MagickColor("#5b4636")).Rectangle(575, 500, 625, 600)
            .FillColor(new MagickColor("#fff4c2")).Circle(980, 160, 1040, 160)
            .Draw(image);
        image.ColorType = ColorType.TrueColor;
        image.SepiaTone(new Percentage(55));
        image.Format = MagickFormat.Jpeg;
        image.Write(path);
    }
}
