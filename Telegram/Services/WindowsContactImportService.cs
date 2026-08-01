using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Models;
using Windows.ApplicationModel.Contacts;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Telegram.Services
{
    internal sealed class SystemContactImportCandidate
    {
        public ChatViewModel Chat { get; set; }
        public string DisplayName { get; set; }
        public string Phone { get; set; }
        public string Username { get; set; }
        public string Initials { get; set; }
        public string RemoteId { get; set; }
        public string NormalizedPhone { get; set; }
    }

    internal static class WindowsContactImportService
    {
        private const string ContactListName = "Telegram";
        private const string RemoteIdPrefix = "telegram:";
        private const string ImportedRemoteIdsKey = "telegram_imported_windows_contact_remote_ids";
        private const string ImportedPhonesKey = "telegram_imported_windows_contact_phones";
        private const string LocalAppDataUriPrefix = "ms-appdata:///local/";

        public static async Task<List<SystemContactImportCandidate>> FindMissingContactsAsync(IList<ChatViewModel> contacts)
        {
            var result = new List<SystemContactImportCandidate>();
            if (contacts == null || contacts.Count == 0) return result;

            var existing = await LoadExistingContactsAsync();
            var addedPhones = new HashSet<string>();
            var addedRemoteIds = new HashSet<string>();

            for (var i = 0; i < contacts.Count; i++)
            {
                var chat = contacts[i];
                if (chat == null || string.IsNullOrWhiteSpace(chat.Phone))
                    continue;

                var normalizedPhone = NormalizePhone(chat.Phone);
                if (string.IsNullOrEmpty(normalizedPhone))
                    continue;

                var remoteId = BuildRemoteId(chat, normalizedPhone);
                if (ContainsRemoteId(existing.RemoteIds, remoteId) || addedRemoteIds.Contains(remoteId))
                    continue;

                if (ContainsPhone(existing.Phones, normalizedPhone) || addedPhones.Contains(normalizedPhone))
                    continue;

                result.Add(new SystemContactImportCandidate
                {
                    Chat = chat,
                    DisplayName = string.IsNullOrWhiteSpace(chat.Title) ? chat.Phone : chat.Title.Trim(),
                    Phone = chat.Phone,
                    Username = chat.Username,
                    Initials = string.IsNullOrWhiteSpace(chat.IconText) ? BuildInitials(chat.Title) : chat.IconText,
                    RemoteId = remoteId,
                    NormalizedPhone = normalizedPhone
                });
                addedPhones.Add(normalizedPhone);
                addedRemoteIds.Add(remoteId);
            }

            return result;
        }

        public static async Task<int> SaveContactsAsync(IList<SystemContactImportCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return 0;

            var store = await ContactManager.RequestStoreAsync(ContactStoreAccessType.AppContactsReadWrite);
            if (store == null) return 0;

            var contactList = await GetOrCreateContactListAsync(store);
            if (contactList == null) return 0;

            var added = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Phone))
                    continue;

                var contact = new Contact();
                ApplyName(contact, candidate.DisplayName);
                contact.RemoteId = string.IsNullOrEmpty(candidate.RemoteId) ? BuildRemoteId(candidate) : candidate.RemoteId;
                contact.Phones.Add(new ContactPhone
                {
                    Number = candidate.Phone,
                    Kind = ContactPhoneKind.Mobile
                });

                if (!string.IsNullOrWhiteSpace(candidate.Username))
                    contact.Notes = "Telegram: @" + candidate.Username.TrimStart('@');

                try
                {
                    var picture = await CreateContactPictureReferenceAsync(candidate);
                    if (picture != null)
                        contact.SourceDisplayPicture = picture;
                }
                catch
                {
                }

                await contactList.SaveContactAsync(contact);
                RememberImportedContact(contact.RemoteId, string.IsNullOrEmpty(candidate.NormalizedPhone) ? NormalizePhone(candidate.Phone) : candidate.NormalizedPhone);
                added++;
            }

            return added;
        }

        private static async Task<ContactList> GetOrCreateContactListAsync(ContactStore store)
        {
            var lists = await store.FindContactListsAsync();
            if (lists != null)
            {
                for (var i = 0; i < lists.Count; i++)
                {
                    var list = lists[i];
                    if (list != null && string.Equals(list.DisplayName, ContactListName, StringComparison.OrdinalIgnoreCase))
                        return list;
                }
            }

            var created = await store.CreateContactListAsync(ContactListName);
            await created.SaveAsync();
            return created;
        }

        private static async Task<ExistingContactState> LoadExistingContactsAsync()
        {
            var result = new ExistingContactState();
            AddRememberedContacts(result);
            await AddContactsFromStoreAsync(result, ContactStoreAccessType.AllContactsReadOnly);
            await AddContactsFromStoreAsync(result, ContactStoreAccessType.AppContactsReadWrite);
            return result;
        }

        private static async Task AddContactsFromStoreAsync(ExistingContactState result, ContactStoreAccessType accessType)
        {
            if (result == null) return;

            ContactStore store;
            try
            {
                store = await ContactManager.RequestStoreAsync(accessType);
            }
            catch
            {
                return;
            }
            if (store == null) return;

            IReadOnlyList<Contact> contacts;
            try
            {
                contacts = await store.FindContactsAsync();
            }
            catch
            {
                return;
            }
            if (contacts == null) return;

            for (var i = 0; i < contacts.Count; i++)
            {
                var contact = contacts[i];
                AddContactToState(result, contact);
            }
        }

        private static void AddContactToState(ExistingContactState result, Contact contact)
        {
            if (result == null || contact == null) return;

            if (!string.IsNullOrEmpty(contact.RemoteId))
                AddUnique(result.RemoteIds, contact.RemoteId);

            if (contact.Phones == null) return;

            for (var j = 0; j < contact.Phones.Count; j++)
            {
                var phone = contact.Phones[j];
                var normalized = phone == null ? null : NormalizePhone(phone.Number);
                if (!string.IsNullOrEmpty(normalized))
                    AddUnique(result.Phones, normalized);
            }
        }

        private static void AddRememberedContacts(ExistingContactState result)
        {
            if (result == null) return;
            AddSettingItems(result.RemoteIds, ImportedRemoteIdsKey);
            AddSettingItems(result.Phones, ImportedPhonesKey);
        }

        private static void AddSettingItems(List<string> target, string key)
        {
            if (target == null || string.IsNullOrEmpty(key)) return;

            object value;
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out value) || value == null)
                return;

            var text = value as string;
            if (string.IsNullOrEmpty(text)) return;

            var items = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < items.Length; i++)
                AddUnique(target, items[i]);
        }

        private static void RememberImportedContact(string remoteId, string normalizedPhone)
        {
            AddSettingItem(ImportedRemoteIdsKey, remoteId);
            AddSettingItem(ImportedPhonesKey, normalizedPhone);
        }

        private static void AddSettingItem(string key, string item)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(item)) return;

            var values = ApplicationData.Current.LocalSettings.Values;
            object value;
            var text = values.TryGetValue(key, out value) ? value as string : null;
            var items = new List<string>();
            if (!string.IsNullOrEmpty(text))
            {
                var split = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < split.Length; i++)
                    AddUnique(items, split[i]);
            }

            AddUnique(items, item);
            values[key] = string.Join("\n", items.ToArray());
        }

        private static bool ContainsPhone(IList<string> existingPhones, string phone)
        {
            if (existingPhones == null || string.IsNullOrEmpty(phone)) return false;
            for (var i = 0; i < existingPhones.Count; i++)
            {
                if (PhonesMatch(existingPhones[i], phone))
                    return true;
            }
            return false;
        }

        private static bool ContainsRemoteId(IList<string> existingRemoteIds, string remoteId)
        {
            if (existingRemoteIds == null || string.IsNullOrEmpty(remoteId)) return false;
            for (var i = 0; i < existingRemoteIds.Count; i++)
            {
                if (string.Equals(existingRemoteIds[i], remoteId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool PhonesMatch(string existing, string phone)
        {
            if (string.IsNullOrEmpty(existing) || string.IsNullOrEmpty(phone)) return false;
            if (existing == phone) return true;
            if (existing.Length >= 7 && phone.Length >= 7)
                return existing.EndsWith(phone, StringComparison.Ordinal) || phone.EndsWith(existing, StringComparison.Ordinal);
            return false;
        }

        private static string NormalizePhone(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = new List<char>();
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (ch >= '0' && ch <= '9')
                    chars.Add(ch);
            }
            return new string(chars.ToArray());
        }

        private static void ApplyName(Contact contact, string displayName)
        {
            displayName = string.IsNullOrWhiteSpace(displayName) ? "Telegram contact" : displayName.Trim();
            var space = displayName.IndexOf(' ');
            if (space > 0 && space < displayName.Length - 1)
            {
                contact.FirstName = displayName.Substring(0, space);
                contact.LastName = displayName.Substring(space + 1);
            }
            else
            {
                contact.FirstName = displayName;
            }
        }

        private static string BuildRemoteId(SystemContactImportCandidate candidate)
        {
            if (candidate != null && candidate.Chat != null && !string.IsNullOrEmpty(candidate.Chat.PeerKey))
                return RemoteIdPrefix + candidate.Chat.PeerKey;
            return RemoteIdPrefix + NormalizePhone(candidate == null ? null : candidate.Phone);
        }

        private static string BuildInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            name = name.Trim();
            return name.Substring(0, 1).ToUpper();
        }

        private static string BuildRemoteId(ChatViewModel chat, string normalizedPhone)
        {
            if (chat != null && !string.IsNullOrEmpty(chat.PeerKey))
                return RemoteIdPrefix + chat.PeerKey;
            return RemoteIdPrefix + normalizedPhone;
        }

        private static async Task<IRandomAccessStreamReference> CreateContactPictureReferenceAsync(SystemContactImportCandidate candidate)
        {
            var uri = candidate == null || candidate.Chat == null ? null : candidate.Chat.AvatarUri;
            if (string.IsNullOrWhiteSpace(uri)) return null;

            try
            {
                if (uri.StartsWith(LocalAppDataUriPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var file = await GetLocalAppDataFileAsync(uri.Substring(LocalAppDataUriPrefix.Length));
                    if (file != null)
                        return RandomAccessStreamReference.CreateFromFile(file);
                }

                return RandomAccessStreamReference.CreateFromUri(new Uri(uri));
            }
            catch
            {
                return null;
            }
        }

        private static async Task<StorageFile> GetLocalAppDataFileAsync(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;

            relativePath = Uri.UnescapeDataString(relativePath).Replace('\\', '/');
            var parts = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            StorageFolder folder = ApplicationData.Current.LocalFolder;
            for (var i = 0; i < parts.Length - 1; i++)
                folder = await folder.GetFolderAsync(parts[i]);

            return await folder.GetFileAsync(parts[parts.Length - 1]);
        }

        private static void AddUnique(List<string> list, string value)
        {
            if (list == null || string.IsNullOrEmpty(value)) return;
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            list.Add(value);
        }

        private sealed class ExistingContactState
        {
            public readonly List<string> Phones = new List<string>();
            public readonly List<string> RemoteIds = new List<string>();
        }
    }
}
