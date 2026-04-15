using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using CalendarParse.Services;

namespace CalendarParse.Platforms.Android;

/// <summary>
/// Android implementation of <see cref="IMessagingAppPickerService"/>.
/// Discovers installed messaging apps, reads SMS threads, and searches contacts.
/// </summary>
public sealed class AndroidMessagingAppPickerService : IMessagingAppPickerService
{
    // Well-known third-party messaging packages to include even if they don't handle the sms: scheme.
    private static readonly string[] KnownMessagingPackages =
    [
        "com.whatsapp",
        "com.whatsapp.w4b",
        "org.telegram.messenger",
        "org.thoughtcrime.securesms",
        "com.facebook.orca",
        "com.facebook.mlite",
        "com.viber.voip",
        "com.discord",
        "com.instagram.android",
        "jp.naver.line.android",
        "com.kakao.talk",
        "com.snapchat.android",
    ];

    // ── GetMessagingAppsAsync ─────────────────────────────────────────────────

    public Task<IReadOnlyList<MessagingAppInfo>> GetMessagingAppsAsync() => Task.Run(() =>
    {
        var context = global::Android.App.Application.Context;
        var pm      = context.PackageManager!;

        var packages    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var smsPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Apps that handle the sms: URI scheme (native SMS clients)
        var smsIntent      = new Intent(Intent.ActionSendto, global::Android.Net.Uri.Parse("smsto:"));
        var smsActivities  = pm.QueryIntentActivities(smsIntent, PackageManager.ResolveInfoFlags.Of(0));
        foreach (var ri in smsActivities)
        {
            if (ri.ActivityInfo?.PackageName is { } pkg)
            {
                packages.Add(pkg);
                smsPackages.Add(pkg);
            }
        }

        // 2. System default SMS app (may duplicate the above but ensures it's included)
        var defaultSms = global::Android.Provider.Telephony.Sms.GetDefaultSmsPackage(context);
        if (defaultSms != null)
        {
            packages.Add(defaultSms);
            smsPackages.Add(defaultSms);
        }

        // 3. Known third-party apps — include only if installed
        foreach (var pkg in KnownMessagingPackages)
        {
            try
            {
                pm.GetPackageInfo(pkg, (PackageInfoFlags)0);
                packages.Add(pkg);
            }
            catch (PackageManager.NameNotFoundException) { /* not installed */ }
        }

        // Build result list
        var result = new List<MessagingAppInfo>(packages.Count);
        foreach (var pkg in packages)
        {
            try
            {
                var appInfo = pm.GetPackageInfo(pkg, (PackageInfoFlags)0)?.ApplicationInfo;
                if (appInfo is null) continue;
                var label   = pm.GetApplicationLabel(appInfo)?.ToString() ?? pkg;
                var icon    = TryGetIconPng(pm, pkg);
                result.Add(new MessagingAppInfo(pkg, label, icon, smsPackages.Contains(pkg)));
            }
            catch { /* package removed between queries — skip */ }
        }

        result.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return (IReadOnlyList<MessagingAppInfo>)result;
    });

    // ── GetSmsThreadsAsync ────────────────────────────────────────────────────

    public Task<IReadOnlyList<SmsThreadInfo>> GetSmsThreadsAsync() => Task.Run(() =>
    {
        var context    = global::Android.App.Application.Context;
        var inboxUri   = global::Android.Provider.Telephony.Sms.Inbox.ContentUri!;
        var projection = new[] { "thread_id", "address", "body" };

        using var cursor = context.ContentResolver!.Query(
            inboxUri, projection, null, null, "date DESC");

        if (cursor is null) return (IReadOnlyList<SmsThreadInfo>)Array.Empty<SmsThreadInfo>();

        var idxThread  = cursor.GetColumnIndex("thread_id");
        var idxAddress = cursor.GetColumnIndex("address");
        var idxBody    = cursor.GetColumnIndex("body");

        var seenThreads = new HashSet<long>();
        var threads     = new List<SmsThreadInfo>();

        while (cursor.MoveToNext() && threads.Count < 50)
        {
            var threadId = idxThread  >= 0 ? cursor.GetLong(idxThread)     : 0;
            var address  = idxAddress >= 0 ? cursor.GetString(idxAddress) ?? "" : "";
            var body     = idxBody    >= 0 ? cursor.GetString(idxBody)    ?? "" : "";

            if (!seenThreads.Add(threadId)) continue; // deduplicate by thread

            var snippet     = body.Length > 80 ? body[..80] + "…" : body;
            var displayName = LookupContactName(context, address) ?? address;

            threads.Add(new SmsThreadInfo(threadId, displayName, snippet, address));
        }

        return (IReadOnlyList<SmsThreadInfo>)threads;
    });

    // ── SearchContactsAsync ───────────────────────────────────────────────────

    public Task<IReadOnlyList<ContactInfo>> SearchContactsAsync(
        string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<ContactInfo>>(Array.Empty<ContactInfo>());

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            const string colDisplayName = "display_name";
            const string colPhoneNumber = "number";

            var context    = global::Android.App.Application.Context;
            var uri        = global::Android.Provider.ContactsContract.CommonDataKinds.Phone.ContentUri!;
            var projection = new string[] { colDisplayName, colPhoneNumber };
            var selection     = $"{colDisplayName} LIKE ?";
            var selectionArgs = new[] { $"%{query}%" };
            var sortOrder     = $"{colDisplayName} ASC";

            using var cursor = context.ContentResolver!.Query(
                uri, projection, selection, selectionArgs, sortOrder);

            if (cursor is null)
                return (IReadOnlyList<ContactInfo>)Array.Empty<ContactInfo>();

            var results = new List<ContactInfo>();
            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (cursor.MoveToNext() && results.Count < 20)
            {
                ct.ThrowIfCancellationRequested();
                var name   = cursor.GetString(0) ?? "";
                var number = cursor.GetString(1) ?? "";
                if (!string.IsNullOrEmpty(name) && seen.Add(name))
                    results.Add(new ContactInfo(name, number));
            }

            return (IReadOnlyList<ContactInfo>)results;
        }, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[]? TryGetIconPng(PackageManager pm, string packageName, int size = 48)
    {
        try
        {
            var drawable = pm.GetApplicationIcon(packageName);
            if (drawable is null) return null;

            var bm = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!);
            var canvas = new Canvas(bm);
            drawable.SetBounds(0, 0, size, size);
            drawable.Draw(canvas);

            using var ms = new MemoryStream();
            bm.Compress(Bitmap.CompressFormat.Png!, 90, ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private static string? LookupContactName(Context context, string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        try
        {
            var lookupUri = global::Android.Net.Uri.WithAppendedPath(
                global::Android.Provider.ContactsContract.PhoneLookup.ContentFilterUri,
                global::Android.Net.Uri.Encode(address));

        if (lookupUri is null) return null;

            var projection = new[]
            {
                global::Android.Provider.ContactsContract.PhoneLookup.InterfaceConsts.DisplayName
            };

            using var cursor = context.ContentResolver!.Query(lookupUri, projection, null, null, null);
            if (cursor?.MoveToFirst() == true)
                return cursor.GetString(0);
        }
        catch { /* fallback to raw address */ }

        return null;
    }
}
