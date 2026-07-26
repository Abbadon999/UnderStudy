using System.Text;
using ArchiSteamFarm.Core;

namespace UnderStudy;

/// <summary>HTTP GET via ASF's own WebBrowser — a private HttpClient hits overloads ASF's trimmed runtime cuts.</summary>
internal static class Net {
	internal static async Task<string?> GetString(Uri uri) {
		try {
			if (ASF.WebBrowser is not { } browser) {
				return null;
			}

			ArchiSteamFarm.Web.Responses.BinaryResponse? response = await browser.UrlGetToBinary(uri).ConfigureAwait(false);

			if (response?.Content is not { } content) {
				return null;
			}

			byte[] bytes = new byte[content.Count];
			int i = 0;

			foreach (byte b in content) {
				bytes[i++] = b;
			}

			return Encoding.UTF8.GetString(bytes);
		} catch {
			return null;
		}
	}
}
