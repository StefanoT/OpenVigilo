using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.Outlook;

public sealed class ClassicOutlookComClient(
    IOutlookStaDispatcher dispatcher,
    ILogger<ClassicOutlookComClient> logger) : IOutlookClient
{
    private const string InternetMessageIdUnicode = "http://schemas.microsoft.com/mapi/proptag/0x1035001F";
    private const string InternetMessageIdAnsi = "http://schemas.microsoft.com/mapi/proptag/0x1035001E";
    private readonly RunningOutlookApplicationProvider _provider = new();

    private static readonly IReadOnlyDictionary<string, int> CategoryColors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [TrackedItemCategories.DueToday] = 3,
        [TrackedItemCategories.WaitingForMyReply] = 8,
        [TrackedItemCategories.NeedsReview] = 6,
        [TrackedItemCategories.Upcoming] = 11,
        [TrackedItemCategories.CommercialOffers] = 9,
        [TrackedItemCategories.Snoozed] = 7,
        [TrackedItemCategories.Dismissed] = 13,
        [TrackedItemCategories.Done] = 10
    };

    public Task<OutlookConnectionInfo> ProbeAsync(CancellationToken cancellationToken) =>
        InvokeLoggedAsync("Probe", () =>
        {
            object? app = null;
            try
            {
                app = _provider.GetRunningApplication();
                dynamic outlook = app;
                string? version = Convert.ToString(outlook.Version, CultureInfo.InvariantCulture);
                return new OutlookConnectionInfo(OutlookIntegrationStatus.Connected, version);
            }
            catch (OutlookUnavailableException ex)
            {
                return new OutlookConnectionInfo(ToStatus(ex.Code), null, ex.Message);
            }
            catch (Exception ex)
            {
                var mapped = MapException(ex, OutlookErrorCodes.ComUnavailable, "Classic Outlook is temporarily unavailable.");
                return new OutlookConnectionInfo(mapped.Code == OutlookErrorCodes.PermissionDenied
                    ? OutlookIntegrationStatus.PermissionDenied : OutlookIntegrationStatus.TemporarilyUnavailable, null, mapped.Message);
            }
            finally { Release(app); }
        }, cancellationToken);

    public Task<IReadOnlyList<OutlookStoreInfo>> GetStoresAsync(CancellationToken cancellationToken) =>
        InvokeLoggedAsync<IReadOnlyList<OutlookStoreInfo>>("EnumerateStores", () =>
        {
            object? app = null; object? session = null; object? stores = null;
            try
            {
                app = _provider.GetRunningApplication();
                dynamic outlook = app;
                session = outlook.Session;
                dynamic ns = session;
                stores = ns.Stores;
                dynamic collection = stores;
                var result = new List<OutlookStoreInfo>();
                for (var index = 1; index <= (int)collection.Count; index++)
                {
                    object? store = null;
                    try
                    {
                        store = collection.Item(index);
                        dynamic value = store;
                        result.Add(new OutlookStoreInfo((string)value.StoreID, (string)value.DisplayName, TryGetStoreAddress(value)));
                    }
                    finally { Release(store); }
                }
                return result;
            }
            catch (Exception ex) { throw MapException(ex, OutlookErrorCodes.ComUnavailable, "Could not enumerate Classic Outlook stores."); }
            finally { Release(stores); Release(session); Release(app); }
        }, cancellationToken);

    public Task EnsureManagedCategoriesAsync(OutlookStoreBinding binding, CancellationToken cancellationToken) =>
        InvokeLoggedAsync("EnsureCategories", () =>
        {
            object? app = null; object? session = null; object? categories = null;
            try
            {
                app = _provider.GetRunningApplication();
                dynamic outlook = app;
                session = outlook.Session;
                dynamic ns = session;
                if (!StoreExists(ns, binding.OutlookStoreId))
                    throw new OutlookIntegrationException(OutlookErrorCodes.StoreNotFound, "The configured Outlook store is not available.", false);
                categories = ns.Categories;
                dynamic collection = categories;
                foreach (var categoryName in OutlookManagedCategories.All)
                {
                    if (!CategoryExists(collection, categoryName)) collection.Add(categoryName, CategoryColors[categoryName]);
                }
                return true;
            }
            catch (Exception ex) { throw MapException(ex, OutlookErrorCodes.CategorySetupFailed, "Could not create or repair Outlook categories."); }
            finally { Release(categories); Release(session); Release(app); }
        }, cancellationToken);

    public Task<OutlookMessageMatchResult> FindMessageAsync(OutlookStoreBinding binding, OutlookMessageIdentity identity, CancellationToken cancellationToken) =>
        InvokeLoggedAsync("FindMessage", () => FindMessage(binding, identity), cancellationToken);

    public Task ApplyCategoriesAsync(OutlookItemReference item, IReadOnlySet<string> desiredCategories, CancellationToken cancellationToken) =>
        InvokeLoggedAsync("ApplyCategories", () =>
        {
            object? app = null; object? session = null; object? mail = null;
            try
            {
                app = _provider.GetRunningApplication();
                dynamic outlook = app;
                session = outlook.Session;
                dynamic ns = session;
                mail = ns.GetItemFromID(item.EntryId, item.StoreId);
                dynamic message = mail;
                if ((int)message.Class != 43)
                    throw new OutlookIntegrationException(OutlookErrorCodes.MessageNotFound, "The stored Outlook item is no longer a mail message.", false);
                var separator = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
                var merge = OutlookCategorySet.Merge(Convert.ToString(message.Categories, CultureInfo.CurrentCulture), desiredCategories, separator);
                if (merge.IsChanged)
                {
                    message.Categories = merge.Serialized;
                    try { message.Save(); }
                    catch (Exception ex) { throw MapException(ex, OutlookErrorCodes.ItemSaveFailed, "Outlook could not save the message categories."); }
                }
                return true;
            }
            catch (Exception ex) { throw MapException(ex, OutlookErrorCodes.CategoryApplyFailed, "Could not apply Outlook categories."); }
            finally { Release(mail); Release(session); Release(app); }
        }, cancellationToken);

    private OutlookMessageMatchResult FindMessage(OutlookStoreBinding binding, OutlookMessageIdentity identity)
    {
        object? app = null; object? session = null; object? store = null; object? inbox = null; object? items = null;
        try
        {
            app = _provider.GetRunningApplication();
            dynamic outlook = app;
            session = outlook.Session;
            dynamic ns = session;
            store = FindStore(ns, binding.OutlookStoreId);
            if (store is null) throw new OutlookIntegrationException(OutlookErrorCodes.StoreNotFound, "The configured Outlook store is not available.", false);

            if (!string.IsNullOrWhiteSpace(identity.PersistedEntryId)
                && string.Equals(identity.PersistedStoreId, binding.OutlookStoreId, StringComparison.Ordinal))
            {
                object? fastItem = null;
                try
                {
                    fastItem = ns.GetItemFromID(identity.PersistedEntryId, binding.OutlookStoreId);
                    if (IsVerifiedMail(fastItem, binding.OutlookStoreId, identity.InternetMessageId))
                        return new OutlookMessageMatchResult(OutlookMessageMatchStatus.Found, BuildReference(fastItem, binding.OutlookStoreId, OutlookMatchMethod.EntryId));
                }
                catch (COMException) { }
                finally { Release(fastItem); }
            }

            dynamic selectedStore = store;
            inbox = selectedStore.GetDefaultFolder(6);
            dynamic folder = inbox;
            items = folder.Items;
            dynamic collection = items;
            var matches = FindCandidates(collection, identity, binding.OutlookStoreId);
            return OutlookMatchPolicy.Select(matches);
        }
        catch (Exception ex) { throw MapException(ex, OutlookErrorCodes.ComUnavailable, "Could not search the configured Outlook Inbox."); }
        finally { Release(items); Release(inbox); Release(store); Release(session); Release(app); }
    }

    private static List<OutlookItemReference> FindCandidates(dynamic items, OutlookMessageIdentity identity, string storeId)
    {
        var normalizedId = OutlookMessageId.Normalize(identity.InternetMessageId);
        object? candidates = null;
        try
        {
            if (normalizedId is not null)
            {
                try
                {
                    var escaped = normalizedId.Replace("'", "''", StringComparison.Ordinal);
                    candidates = items.Restrict($"@SQL=\"{InternetMessageIdUnicode}\" = '{escaped}'");
                    var restricted = InspectCandidates(candidates, identity, storeId, 50, OutlookMatchMethod.InternetMessageId);
                    if (restricted.Count > 0) return restricted;
                }
                catch (COMException) { }
                finally { Release(candidates); candidates = null; }
            }

            items.Sort("[ReceivedTime]", true);
            return InspectCandidates(items, identity, storeId, 500, OutlookMatchMethod.BoundedFallback);
        }
        finally { Release(candidates); }
    }

    private static List<OutlookItemReference> InspectCandidates(dynamic items, OutlookMessageIdentity identity, string storeId, int maximum, OutlookMatchMethod method)
    {
        var found = new List<OutlookItemReference>();
        var count = Math.Min((int)items.Count, maximum);
        for (var index = 1; index <= count; index++)
        {
            object? item = null;
            try
            {
                item = items.Item(index);
                dynamic mail = item;
                if ((int)mail.Class != 43 || !IsPlausible(mail, identity)) continue;
                found.Add(BuildReference(item, storeId, method));
                if (found.Count > 1) break;
            }
            catch (COMException) { }
            finally { Release(item); }
        }
        return found;
    }

    private static bool IsPlausible(dynamic mail, OutlookMessageIdentity identity)
    {
        var wantedId = OutlookMessageId.Normalize(identity.InternetMessageId);
        var actualId = ReadInternetMessageId(mail);
        if (wantedId is not null && actualId is not null) return OutlookMessageId.Equals(wantedId, actualId);

        if (identity.ReceivedAtUtc is null || string.IsNullOrWhiteSpace(identity.SenderAddress)) return false;
        var received = new DateTimeOffset((DateTime)mail.ReceivedTime).ToUniversalTime();
        if ((received - identity.ReceivedAtUtc.Value).Duration() > TimeSpan.FromMinutes(15)) return false;
        var sender = Convert.ToString(mail.SenderEmailAddress, CultureInfo.InvariantCulture);
        if (!string.Equals(sender, identity.SenderAddress, StringComparison.OrdinalIgnoreCase)) return false;
        return !string.IsNullOrWhiteSpace(identity.Subject)
            && string.Equals(Convert.ToString(mail.Subject, CultureInfo.InvariantCulture), identity.Subject, StringComparison.Ordinal);
    }

    private static bool IsVerifiedMail(object item, string configuredStoreId, string? expectedId)
    {
        dynamic mail = item;
        return OutlookMatchPolicy.IsValidFastPath(
            (int)mail.Class == 43,
            GetItemStoreId(mail),
            configuredStoreId,
            expectedId,
            ReadInternetMessageId(mail));
    }

    private static string GetItemStoreId(dynamic mail)
    {
        object? parent = null; object? store = null;
        try
        {
            parent = mail.Parent;
            dynamic folder = parent;
            store = folder.Store;
            return Convert.ToString(((dynamic)store).StoreID, CultureInfo.InvariantCulture) ?? "";
        }
        catch (COMException) { return ""; }
        finally { Release(store); Release(parent); }
    }

    private static string? ReadInternetMessageId(dynamic mail)
    {
        object? accessor = null;
        try
        {
            accessor = mail.PropertyAccessor;
            dynamic properties = accessor;
            try { return OutlookMessageId.Normalize(Convert.ToString(properties.GetProperty(InternetMessageIdUnicode), CultureInfo.InvariantCulture)); }
            catch (COMException) { return OutlookMessageId.Normalize(Convert.ToString(properties.GetProperty(InternetMessageIdAnsi), CultureInfo.InvariantCulture)); }
        }
        catch (COMException) { return null; }
        finally { Release(accessor); }
    }

    private static OutlookItemReference BuildReference(object item, string storeId, OutlookMatchMethod method)
    {
        object? parent = null;
        try
        {
            dynamic mail = item;
            parent = mail.Parent;
            dynamic folder = parent;
            return new OutlookItemReference((string)mail.EntryID, storeId, Convert.ToString(folder.EntryID, CultureInfo.InvariantCulture), method);
        }
        finally { Release(parent); }
    }

    private static object? FindStore(dynamic session, string storeId)
    {
        object? stores = null;
        try
        {
            stores = session.Stores;
            dynamic collection = stores;
            for (var index = 1; index <= (int)collection.Count; index++)
            {
                object? store = collection.Item(index);
                var matched = false;
                try
                {
                    matched = string.Equals((string)((dynamic)store).StoreID, storeId, StringComparison.Ordinal);
                    if (matched) return store;
                }
                finally { if (!matched) Release(store); }
            }
            return null;
        }
        finally { Release(stores); }
    }

    private static bool StoreExists(dynamic session, string storeId)
    {
        var store = FindStore(session, storeId);
        try { return store is not null; }
        finally { Release(store); }
    }

    private static bool CategoryExists(dynamic categories, string categoryName)
    {
        for (var index = 1; index <= (int)categories.Count; index++)
        {
            object? category = null;
            try
            {
                category = categories.Item(index);
                if (string.Equals((string)((dynamic)category).Name, categoryName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            finally { Release(category); }
        }
        return false;
    }

    private static string? TryGetStoreAddress(dynamic store)
    {
        object? accessor = null;
        try
        {
            accessor = store.PropertyAccessor;
            return Convert.ToString(((dynamic)accessor).GetProperty("http://schemas.microsoft.com/mapi/proptag/0x39FE001F"), CultureInfo.InvariantCulture);
        }
        catch (COMException) { return null; }
        finally { Release(accessor); }
    }

    private Task<T> InvokeLoggedAsync<T>(string operation, Func<T> action, CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            try { return action(); }
            finally { logger.LogDebug("Outlook operation completed. Operation={Operation} DurationMs={DurationMs}", operation, stopwatch.ElapsedMilliseconds); }
        }, cancellationToken);

    private Task InvokeLoggedAsync(string operation, Func<bool> action, CancellationToken cancellationToken) =>
        InvokeLoggedAsync<bool>(operation, action, cancellationToken);

    private static OutlookIntegrationStatus ToStatus(string code) => code switch
    {
        OutlookErrorCodes.UnsupportedPlatform => OutlookIntegrationStatus.UnsupportedPlatform,
        OutlookErrorCodes.ClassicNotInstalled => OutlookIntegrationStatus.ClassicOutlookNotInstalled,
        OutlookErrorCodes.NotRunning => OutlookIntegrationStatus.OutlookNotRunning,
        _ => OutlookIntegrationStatus.TemporarilyUnavailable
    };

    private static OutlookIntegrationException MapException(Exception exception, string fallbackCode, string fallbackMessage)
    {
        if (exception is OutlookIntegrationException known) return known;
        if (exception is OutlookUnavailableException unavailable)
            return new OutlookIntegrationException(unavailable.Code, unavailable.Message, unavailable.Code == OutlookErrorCodes.NotRunning, unavailable);
        var com = exception as COMException ?? exception.InnerException as COMException;
        if (com is not null)
        {
            return com.HResult switch
            {
                unchecked((int)0x80010001) => new OutlookIntegrationException(OutlookErrorCodes.CallRejected, "Classic Outlook rejected the call because it is busy.", true, com),
                unchecked((int)0x8001010A) => new OutlookIntegrationException(OutlookErrorCodes.Busy, "Classic Outlook is busy.", true, com),
                unchecked((int)0x80070005) => new OutlookIntegrationException(OutlookErrorCodes.PermissionDenied, "Classic Outlook denied access.", false, com),
                _ => new OutlookIntegrationException(fallbackCode, $"{fallbackMessage} (HRESULT 0x{com.HResult:X8})", true, com)
            };
        }
        return new OutlookIntegrationException(fallbackCode, fallbackMessage, true, exception);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
