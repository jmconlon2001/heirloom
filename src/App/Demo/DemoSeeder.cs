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
            (string Caption, Scene Scene, Guid[] People)[] pictures, Guid? writtenBy = null, DateTime? finished = null)
        {
            var prompt = await db.Prompts.SingleAsync(p => p.Key == key);
            var m = await memories.GetOrCreateForPromptAsync(teller.Id, prompt, writtenBy ?? teller.Id);
            await memories.SaveAsync(m.Id, new MemoryEdit(null, body, when, place), writtenBy ?? teller.Id, VersionKind.Save);
            foreach (var pic in pictures)
            {
                var file = Path.Combine(photos, $"{Guid.NewGuid():N}.jpg");
                Picture(file, pic.Scene);
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
            [("The farmhouse, about 1935", Scene.Farmhouse, [])], finished: now.AddDays(-40));
        var meeting = await Story(rose, "love-meeting",
            "It was the Summit County Fair, the summer of 1950. Joe had entered his tomatoes, and he won the blue ribbon. He walked right over and pinned it on my coat. I told him that was the boldest thing I'd ever seen, and he said, \"Then you'd better let me buy you a lemonade.\"\n\nWe were married two years later. He grew tomatoes every summer for the rest of his life.",
            new FuzzyDate(1950, 8, null, false, null, null), "Summit County Fair", Visibility.AllFamily, [joe.Id, roseP.Id],
            [("Joe and me at the fair, 1950", Scene.Fair, [joe.Id, roseP.Id]),
             ("Our wedding day, June 1952", Scene.Wedding, [joe.Id, roseP.Id])], finished: now.AddDays(-9));
        await Story(rose, "food-recipe",
            "Mother's apple butter. Two pecks of apples, a gallon of cider, three cups of sugar, cinnamon and cloves. You stir it in the copper kettle all afternoon so it doesn't scorch, and everybody takes a turn.\n\nThe secret is patience. And a little more cinnamon than you think.",
            new FuzzyDate(null, null, null, false, 1940, null), null, Visibility.AllFamily, [edna.Id],
            [("The recipe card in Mother's handwriting", Scene.RecipeCard, [edna.Id])], finished: now.AddDays(-3));
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

    private enum Scene { Farmhouse, Fair, Wedding, RecipeCard }

    /// <summary>
    /// A simple stand-in for an old family snapshot: a grey-toned scene, then film grain, a soft focus,
    /// a warm sepia tint, a vignette and a white print border. No text (fonts vary between systems).
    /// </summary>
    private static void Picture(string path, Scene scene)
    {
        static MagickColor G(int v) => new((byte)v, (byte)v, (byte)v);
        using var image = new MagickImage(G(200), 1200, 900);
        var d = new Drawables();
        switch (scene)
        {
            case Scene.Farmhouse:
                d.FillColor(G(205)).Rectangle(0, 0, 1200, 560)
                 .FillColor(G(120)).Rectangle(0, 560, 1200, 900)
                 // barn
                 .FillColor(G(90)).Rectangle(760, 380, 1060, 600)
                 .FillColor(G(70)).Polygon(new PointD(740, 390), new PointD(910, 270), new PointD(1080, 390))
                 .FillColor(G(150)).Rectangle(870, 470, 950, 600)
                 // house
                 .FillColor(G(60)).Polygon(new PointD(230, 420), new PointD(410, 290), new PointD(590, 420))
                 .FillColor(G(225)).Rectangle(260, 420, 560, 620)
                 .FillColor(G(70)).Rectangle(385, 510, 435, 620)
                 .FillColor(G(110)).Rectangle(290, 460, 345, 510).Rectangle(475, 460, 530, 510)
                 // tree
                 .FillColor(G(80)).Rectangle(120, 430, 145, 620)
                 .FillColor(G(95)).Circle(132, 380, 132, 290)
                 // fence
                 .StrokeColor(G(235)).StrokeWidth(6).Line(0, 700, 1200, 690).Line(0, 740, 1200, 730);
                for (var x = 20; x < 1200; x += 80) d.Line(x, 680, x, 760);
                break;
            case Scene.Fair:
                d.FillColor(G(215)).Rectangle(0, 0, 1200, 600)
                 .FillColor(G(150)).Rectangle(0, 600, 1200, 900)
                 // Ferris wheel
                 .FillColor(MagickColors.Transparent).StrokeColor(G(90)).StrokeWidth(8).Circle(880, 300, 880, 90);
                for (var a = 0; a < 360; a += 30)
                {
                    var r = a * Math.PI / 180;
                    d.Line(880, 300, 880 + 210 * Math.Cos(r), 300 + 210 * Math.Sin(r));
                }
                d.Line(880, 300, 800, 610).Line(880, 300, 960, 610)
                 // striped tent
                 .StrokeWidth(0).FillColor(G(240)).Polygon(new PointD(80, 600), new PointD(300, 330), new PointD(520, 600));
                for (var x = 80; x < 520; x += 88)
                    d.FillColor(G(110)).Polygon(new PointD(x, 600), new PointD(300, 330), new PointD(x + 44, 600));
                // two people
                Person(d, 560, 640, G(55), G(70));
                Person(d, 660, 650, G(185), G(70));
                break;
            case Scene.Wedding:
                d.FillColor(G(185)).Rectangle(0, 0, 1200, 900)
                 // church doorway
                 .FillColor(G(120)).Rectangle(380, 260, 820, 900)
                 .FillColor(G(75)).Circle(600, 300, 600, 140).Rectangle(440, 300, 760, 900)
                 .FillColor(G(150)).Rectangle(0, 760, 1200, 900);
                // groom in a dark suit, bride in white with a long dress
                Person(d, 520, 520, G(40), G(70));
                d.FillColor(G(245)).Polygon(new PointD(640, 520), new PointD(560, 860), new PointD(760, 860))
                 .FillColor(G(150)).Circle(660, 470, 660, 430)
                 .FillColor(G(230)).Polygon(new PointD(660, 425), new PointD(610, 640), new PointD(710, 640));
                break;
            case Scene.RecipeCard:
                d.FillColor(G(110)).Rectangle(0, 0, 1200, 900)
                 .FillColor(G(70)).Rectangle(0, 0, 1200, 30)
                 .FillColor(G(235)).Rectangle(180, 170, 1020, 730)
                 .StrokeColor(G(170)).StrokeWidth(3);
                for (var y = 290; y < 720; y += 50) d.Line(200, y, 1000, y);
                // handwriting-like squiggles
                d.FillColor(MagickColors.Transparent).StrokeColor(G(60)).StrokeWidth(5);
                var rng = new Random(7);
                for (var y = 280; y < 700; y += 50)
                {
                    var x = 220.0;
                    var end = 640 + rng.Next(0, 340);
                    while (x < end)
                    {
                        var w = 25 + rng.Next(0, 45);
                        d.Bezier(new PointD(x, y), new PointD(x + w / 3.0, y - 22), new PointD(x + 2 * w / 3.0, y + 8), new PointD(x + w, y - 6));
                        x += w + (rng.Next(0, 4) == 0 ? 22 : 4);
                    }
                }
                d.StrokeWidth(7).Bezier(new PointD(330, 230), new PointD(420, 180), new PointD(520, 260), new PointD(700, 215));
                break;
        }
        d.Draw(image);
        image.ColorType = ColorType.TrueColor;
        image.AddNoise(NoiseType.Gaussian, 0.35);
        image.GaussianBlur(0, 1.6);
        image.Grayscale();
        image.Colorize(new MagickColor("#8a5a2b"), new Percentage(28));
        image.Vignette(0, 220, 0, 0);
        image.BorderColor = new MagickColor("#f6f1e6");
        image.Border(48);
        image.Quality = 85;
        image.Format = MagickFormat.Jpeg;
        image.Write(path);
    }

    private static void Person(Drawables d, double x, double y, MagickColor body, MagickColor head)
    {
        d.StrokeWidth(0).FillColor(head).Circle(x, y - 60, x, y - 100)
         .FillColor(body).Rectangle(x - 45, y - 20, x + 45, y + 180)
         .Rectangle(x - 40, y + 180, x - 8, y + 240).Rectangle(x + 8, y + 180, x + 40, y + 240);
    }
}
