using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Api;
using Jellyfin.Api.Constants;
using Jellyfin.Plugin.Dlna.EventArgs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Dlna.PlayTo.Api
{
    /// <summary>
    /// Dlna PlayTo Controller.
    /// </summary>
    [Route("Dlna")]
    [Authorize(Policy = Policies.LocalNetworkAccessPolicy)]
    public class DlnaPlayToController : BaseJellyfinApiController
    {
        /// <summary>
        /// Processes device subscription events.
        /// Has to be a url, as the XML content from devices can be corrupt.
        /// </summary>
        /// <param name="id">Id of the device.</param>
        /// <returns>Event subscription response.</returns>
        [HttpNotify("Eventing/{id}")]
        [ApiExplorerSettings(IgnoreApi = true)] // Ignore in openapi docs
        public async Task<ActionResult> ProcessDeviceNotification([FromRoute, Required] string id)
        {
            try
            {
                using var reader = new StreamReader(Request.Body, Encoding.UTF8);
                var response = await reader.ReadToEndAsync().ConfigureAwait(false);
                await DlnaPlayTo.GetInstance().NotifyDevice(new DlnaEventArgs(id, response)).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch
#pragma warning restore CA1031 // Do not catch general exception types
            {
                // Ignore connection forcible closed messages.
            }

            return Ok();
        }
    }
}
