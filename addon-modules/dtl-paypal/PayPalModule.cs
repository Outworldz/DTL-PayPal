/*
 * Copyright (c) DeepThink Pty Ltd, http://www.deepthinklabs.com/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Web;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using Nwc.XmlRpc;

using Mono.Addins; // I hate you Mono.Addins

[assembly: Addin("PayPal", "0.1")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("OpenSim Addin for PayPal currency module")]
[assembly: AddinAuthor("DeepThink Pty Ltd")]

namespace DeepThink.PayPal
{
    [Extension(Path="/OpenSim/RegionModules",NodeName="RegionModule", Id = "PayPalModule")]
    public class PayPalModule : ISharedRegionModule, IMoneyModule
    {
        private string m_ppurl = "www.paypal.com"; // Change to www.sandbox.paypal.com for testing.

        private bool m_active;
        private bool m_enabled;

        private readonly object m_setupLock = new object();
        private bool m_setup;

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly Dictionary<UUID,string> m_usersemail = new Dictionary<UUID, string>();
        private readonly Dictionary<UUID,string> m_groupsemail = new Dictionary<UUID, string>();

        // Fetch a user's PayPal receiver email from the grid's User service (their profile email) when no explicit
        // [PayPal Users] entry exists for them - ported from Mod-PayPal (github.com/SnoopyPfeffer/Mod-PayPal).
        private bool m_allowGridEmails;

        // Allow group-owned objects (OwnerID == GroupID) to pay out to a [PayPal Groups] receiver email instead of
        // the object owner's - ported from Mod-PayPal.
        private bool m_allowGroups;

        // Feature gap flagged by Fred while writing the manual: a sale amount too small to cover PayPal's own
        // transaction fees produces a confusing/misleading error on PayPal's own checkout page instead of a clear
        // one. Rejecting too-small amounts up front (in cents, matching PayPalTransaction.Amount/salePrice's own
        // unit) with our own clear message avoids that. 0 (the default) means no minimum is enforced.
        private int m_minimumAmountCents;

        // Fred hand-added [PayPal] BalanceOnEntry=true to the real proto, citing the original module author's own
        // notes (confirmed: SnoopyPfeffer's write-up at
        // https://snoopypfeffer.wordpress.com/2009/11/18/paypal-money-module/ states "It is important that you use
        // [this] ... to ensure that the balance is sent each time an avatar enters the region", since without it a
        // balance can appear to vanish across a teleport/hypergrid jump). Ported from Mod-PayPal's own
        // MakeRootAgent()/OnMakeRootAgent hook (github.com/SnoopyPfeffer/Mod-PayPal).
        private bool m_balanceOnEntry;

        private IConfigSource m_config;

        private readonly List<Scene> m_scenes = new List<Scene>();

        private readonly Dictionary<UUID,PayPalTransaction> m_transactionsInProgress = new Dictionary<UUID, PayPalTransaction>();

        // How long an object/land item stays locked against a new PayPal transaction while a previous one is still
        // in flight, before it's treated as abandoned (buyer never completed PayPal's checkout) and released.
        // There was previously no cleanup at all for an abandoned transaction - the entry would sit in
        // m_transactionsInProgress forever, and (once locking was added) would have locked its object permanently.
        private static readonly TimeSpan TransactionLockTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Locking for PayPal transactions while buying land or original objects (feature gap flagged by Fred while
        /// writing the manual): without this, a second buy/pay attempt on an object that already has a PayPal
        /// transaction in flight would create a SECOND concurrent transaction against the same object, and whichever
        /// IPN callback lands second could deliver/pay out twice for one item, or deliver to the wrong buyer.
        /// Returns true (and rejects the new attempt) if objectID already has a non-expired transaction in progress;
        /// as a side effect, also prunes any expired (abandoned) transactions it encounters for any object, so a
        /// buyer who never completes checkout doesn't lock that item forever.
        /// </summary>
        private bool IsObjectLocked(UUID objectID)
        {
            if (objectID == UUID.Zero)
                return false; // User-to-user payments (no ObjectID) are never locked against each other.

            lock (m_transactionsInProgress)
            {
                List<UUID> expired = null;
                bool locked = false;

                foreach (KeyValuePair<UUID, PayPalTransaction> kvp in m_transactionsInProgress)
                {
                    if (DateTime.UtcNow - kvp.Value.CreatedAtUtc > TransactionLockTimeout)
                    {
                        (expired ??= new List<UUID>()).Add(kvp.Key);
                        continue;
                    }

                    if (kvp.Value.ObjectID == objectID)
                        locked = true;
                }

                if (expired != null)
                {
                    foreach (UUID txId in expired)
                    {
                        m_log.Warn("[PayPal] Transaction " + txId + " abandoned (no IPN within " +
                                   TransactionLockTimeout.TotalMinutes + " minutes) - releasing its lock.");
                        m_transactionsInProgress.Remove(txId);
                    }
                }

                return locked;
            }
        }

        #region PayPal Currency

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>Thanks to Melanie for reminding me about 
        /// EventManager.OnMoneyTransfer being the critical function,
        /// and not ApplyCharge.</remarks>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void EventManager_OnMoneyTransfer(object sender, EventManager.MoneyTransferArgs e)
        {
            if(!m_active)
                return;

            IClientAPI user = null;
            Scene scene = null;

            // Find the user's controlling client.
            lock(m_scenes)
            {
                foreach (Scene sc in m_scenes)
                {
                    List<ScenePresence> avs =
                        sc.GetScenePresences().FindAll(
                            x =>
                            (x.UUID == e.sender && x.IsChildAgent == false)
                            );

                    if(avs.Count > 0)
                    {
                        if(avs.Count > 1)
                        {
                            m_log.Warn("[PayPal] Multiple avatars with same UUID! Aborting transaction.");
                            return;
                        }

                        // Found the client,
                        // and their root scene.
                        user = avs[0].ControllingClient;
                        scene = sc;
                    }
                }
            }

            if(scene == null || user == null)
            {
                m_log.Warn("[PayPal] Unable to find scene or user! Aborting transaction.");
                return;
            }

            PayPalTransaction txn;

            if (e.transactiontype == 5008)
            {
                // Object was paid, find it.
                SceneObjectPart sop = scene.GetSceneObjectPart(e.receiver);
                if (sop == null)
                {
                    m_log.Warn("[PayPal] Unable to find SceneObjectPart that was paid. Aborting transaction.");
                    return;
                }

                if (!TryGetReceiverEmail(sop.OwnerID, sop.GroupID, out string sopEmail))
                {
                    m_log.Warn("[PayPal] No PayPal receiver email found for owner " + sop.OwnerID + ". Aborting transaction.");
                    return;
                }

                if (IsObjectLocked(e.receiver))
                {
                    m_log.Warn("[PayPal] Object " + e.receiver + " already has a PayPal transaction in progress. Rejecting.");
                    user.SendAlertMessage("This item already has a PayPal payment in progress from another transaction. Please try again shortly.");
                    return;
                }

                if (m_minimumAmountCents > 0 && e.amount < m_minimumAmountCents)
                {
                    m_log.Warn("[PayPal] Amount " + ConvertAmountToCurrency(e.amount) + " is below the configured minimum of " +
                               ConvertAmountToCurrency(m_minimumAmountCents) + ". Rejecting.");
                    user.SendAlertMessage("This amount is too small for a PayPal payment (minimum is " +
                                          ConvertAmountToCurrency(m_minimumAmountCents) + " " + PayPalTransaction.CurrencyCode + ").");
                    return;
                }

                txn = new PayPalTransaction(e.sender, sop.OwnerID, sopEmail, e.amount,
                                            scene, e.receiver, e.description + "T:" + e.transactiontype, PayPalTransaction.InternalTransactionType.Payment);
            }
            else
            {
                // Payment to a user.
                if (!TryGetReceiverEmail(e.receiver, UUID.Zero, out string receiverEmail))
                {
                    m_log.Warn("[PayPal] No PayPal receiver email found for " + e.receiver + ". Aborting transaction.");
                    return;
                }

                if (m_minimumAmountCents > 0 && e.amount < m_minimumAmountCents)
                {
                    m_log.Warn("[PayPal] Amount " + ConvertAmountToCurrency(e.amount) + " is below the configured minimum of " +
                               ConvertAmountToCurrency(m_minimumAmountCents) + ". Rejecting.");
                    user.SendAlertMessage("This amount is too small for a PayPal payment (minimum is " +
                                          ConvertAmountToCurrency(m_minimumAmountCents) + " " + PayPalTransaction.CurrencyCode + ").");
                    return;
                }

                txn = new PayPalTransaction(e.sender, e.receiver, receiverEmail, e.amount,
                                            scene, e.description + "T:" + e.transactiontype, PayPalTransaction.InternalTransactionType.Payment);
            }

            // Add transaction to queue
            lock (m_transactionsInProgress)
                m_transactionsInProgress.Add(txn.TxID, txn);

            string baseUrl = m_scenes[0].RegionInfo.ExternalHostName + ":" + m_scenes[0].RegionInfo.HttpPort;

            user.SendLoadURL("PayPal", txn.ObjectID, txn.To, false, "Confirm payment?",
                             "http://" + baseUrl + "/dtlpp/?txn=" + txn.TxID);
        }

        void TransferSuccess(PayPalTransaction transaction)
        {
            if (transaction.InternalType == PayPalTransaction.InternalTransactionType.Payment)
            {
                if (transaction.ObjectID == UUID.Zero)
                {
                    // User 2 User Transaction
                    // Probably should notify them somehow.
                }
                else
                {
                    if (OnObjectPaid != null)
                    {
                        OnObjectPaid(transaction.ObjectID, transaction.From, transaction.Amount);
                    }
                }
            }
            else if (transaction.InternalType == PayPalTransaction.InternalTransactionType.Purchase)
            {
                if (transaction.ObjectID == UUID.Zero)
                {
                    m_log.Error("[PayPal] Unable to find Object bought! UUID Zero.");
                }
                else
                {
                    Scene s = LocateSceneClientIn(transaction.From);
                    SceneObjectPart part = s.GetSceneObjectPart(transaction.ObjectID);
                    if (part == null)
                    {
                        m_log.Error("[PayPal] Unable to find Object bought! UUID = " + transaction.ObjectID);
                        return;
                    }
                    ScenePresence buyerPresence = s.GetScenePresence(transaction.From);
                    if (buyerPresence == null)
                    {
                        m_log.Error("[PayPal] Unable to find buyer! UUID = " + transaction.From);
                        return;
                    }
                    IBuySellModule buySellModule = s.RequestModuleInterface<IBuySellModule>();
                    if (buySellModule == null)
                    {
                        m_log.Error("[PayPal] No IBuySellModule available to complete purchase.");
                        return;
                    }
                    buySellModule.BuyObject(buyerPresence.ControllingClient,
                                       transaction.InternalPurchaseFolderID, part.LocalId,
                                       transaction.InternalPurchaseType, transaction.Amount);
                }
            }
            else
            {
                m_log.Error("[PayPal] Unknown Internal Transaction Type.");
                return;
            }
            // Cleanup.
            lock (m_transactionsInProgress)
                m_transactionsInProgress.Remove(transaction.TxID);
        }

        // Currently hard coded to $0.01 = OS$1
        static decimal ConvertAmountToCurrency(int amount)
        {
            return amount/(decimal) 100;
        }

        /// <summary>
        /// Resolves the PayPal receiver email for a payment recipient. Group-owned objects (OwnerID == GroupID)
        /// are checked against [PayPal Groups] first when AllowGroups is on - ported from Mod-PayPal
        /// (github.com/SnoopyPfeffer/Mod-PayPal). Falls back to the grid's User service profile email when
        /// AllowGridEmails is on and no explicit [PayPal Users] entry exists for the recipient. Returns false
        /// (rather than throwing, as the original raw dictionary indexer did) when no usable email can be found.
        /// </summary>
        internal bool TryGetReceiverEmail(UUID ownerID, UUID groupID, out string email)
        {
            if (m_allowGroups && groupID != UUID.Zero && ownerID == groupID && m_groupsemail.TryGetValue(groupID, out email))
                return true;

            if (m_usersemail.TryGetValue(ownerID, out email))
                return true;

            if (m_allowGridEmails)
            {
                UserAccount account = m_scenes[0].UserAccountService.GetUserAccount(m_scenes[0].RegionInfo.ScopeID, ownerID);
                if (account != null && PayPalHelpers.IsValidEmail(account.Email))
                {
                    email = account.Email;
                    return true;
                }
            }

            email = null;
            return false;
        }

        public Hashtable PayPalUserPage(Hashtable request)
        {
            UUID txnID = new UUID((string) request["txn"]);

            if(!m_transactionsInProgress.ContainsKey(txnID))
            {
                Hashtable ereply = new Hashtable();

                ereply["int_response_code"] = 404; // 200 OK
                ereply["str_response_string"] = "<h1>Invalid Transaction</h1>";
                ereply["content_type"] = "text/html";

                return ereply;
            }

            PayPalTransaction txn = m_transactionsInProgress[txnID];

            string baseUrl = m_scenes[0].RegionInfo.ExternalHostName + ":" + m_scenes[0].RegionInfo.HttpPort;

            // Ouch. (This is the PayPal Request URL)
            string url = "https://" + m_ppurl + "/cgi-bin/webscr?cmd=_xclick" +
                         "&business=" + HttpUtility.UrlEncode(txn.SellersEmail) +
                         "&item_name=" + HttpUtility.UrlEncode(txn.Description) +
                         "&item_number=" + HttpUtility.UrlEncode(txn.TxID.ToString()) +
                         "&amount=" + HttpUtility.UrlEncode(ConvertAmountToCurrency(txn.Amount).ToString()) +
                         "&page_style=" + HttpUtility.UrlEncode("Paypal") +
                         "&no_shipping=" + HttpUtility.UrlEncode("1") +
                         "&return=" + HttpUtility.UrlEncode("http://" + baseUrl + "/dtlpp-return") +
                         "&cancel_return=" + HttpUtility.UrlEncode("http://" + baseUrl + "/dtlpp-cancel") +
                         "&notify_url=" + HttpUtility.UrlEncode("http://" + baseUrl + "/dtlppipn/") +
                         "&no_note=" + HttpUtility.UrlEncode("1") +
                         "&currency_code=" + HttpUtility.UrlEncode("USD") +
                         "&lc=" + HttpUtility.UrlEncode("US") +
                         "&bn=" + HttpUtility.UrlEncode("PP-BuyNowBF") +
                         "&charset=" + HttpUtility.UrlEncode("UTF-8") +
                         "";

            // HTML-encode values that can contain arbitrary user-supplied text (an in-world object's own
            // description, an ini-configured seller email) before substituting them into the served HTML page -
            // ported from Mod-PayPal (github.com/SnoopyPfeffer/Mod-PayPal), which fixes a stored-XSS gap the
            // original 2009-2010 DTL-PayPal had via raw, unescaped string substitution. {BILLINGLINK} is
            // deliberately NOT HTML-encoded here - it's a URL whose own query parameters are already
            // HttpUtility.UrlEncode'd above, and it needs to remain a valid href value, not further-escaped text.
            Dictionary<string,string> replacements = new Dictionary<string, string>();
            replacements.Add("{ITEM}", HttpUtility.HtmlEncode(txn.Description));
            replacements.Add("{AMOUNT}", HttpUtility.HtmlEncode(ConvertAmountToCurrency(txn.Amount).ToString()));
            replacements.Add("{AMOUNTOS}", HttpUtility.HtmlEncode(txn.Amount.ToString()));
            replacements.Add("{CURRENCYCODE}", "USD");
            replacements.Add("{BILLINGLINK}", url);
            replacements.Add("{OBJECTID}", HttpUtility.HtmlEncode(txn.ObjectID.ToString()));
            replacements.Add("{SELLEREMAIL}", HttpUtility.HtmlEncode(txn.SellersEmail));

            

            string template;

            try
            {
                template = File.ReadAllText("paypal-template.htm");
            }
            catch (IOException)
            {
                template = "Error: paypal-template.htm does not exist.";
                m_log.Error("[PayPal] Unable to load template file.");
            }

            foreach (KeyValuePair<string, string> pair in replacements)
            {
                template = template.Replace(pair.Key, pair.Value);
            }

            Hashtable reply = new Hashtable();

            reply["int_response_code"] = 200; // 200 OK
            reply["str_response_string"] = template;
            reply["content_type"] = "text/html";

            return reply;
        }

        // Feature gap flagged by Fred while writing the manual: PayPal's own hosted checkout page redirects the
        // buyer's browser back here via &return=/&cancel_return= after they complete or cancel payment on PayPal's
        // site - the original 2009-2010 DTL-PayPal pointed both at a bare region root URL with no real handler
        // (// TODO: Add in a return page / // TODO: Add in a cancel page, left unfinished for over a decade),
        // which 404'd. These two simple pages replace that. Note the ACTUAL money confirmation/delivery still
        // happens via PayPalIPN (a separate server-to-server callback PayPal makes independently of the buyer's
        // browser) - these pages are purely a friendlier landing screen for the buyer, not part of the payment
        // logic itself.
        public Hashtable PayPalReturnPage(Hashtable request)
        {
            return StaticHtmlPage("paypal-return-template.htm",
                "<html><head><title>Payment Complete</title></head><body>" +
                "<h1>Thank you!</h1>" +
                "<p>Your PayPal payment has been submitted. It may take a few moments for the item or funds to " +
                "arrive in-world once PayPal confirms the transaction.</p>" +
                "<p>You may close this window and return to the viewer.</p>" +
                "</body></html>");
        }

        public Hashtable PayPalCancelPage(Hashtable request)
        {
            return StaticHtmlPage("paypal-cancel-template.htm",
                "<html><head><title>Payment Cancelled</title></head><body>" +
                "<h1>Payment Cancelled</h1>" +
                "<p>Your PayPal payment was not completed. No funds were transferred and no item was delivered.</p>" +
                "<p>You may close this window and return to the viewer.</p>" +
                "</body></html>");
        }

        /// <summary>Serves a static HTML template file from the bin/ working directory (same location/pattern as
        /// paypal-template.htm), falling back to a plain inline page if the file is missing.</summary>
        private static Hashtable StaticHtmlPage(string templateFileName, string fallbackHtml)
        {
            string html;
            try
            {
                html = File.ReadAllText(templateFileName);
            }
            catch (IOException)
            {
                m_log.Error("[PayPal] Unable to load template file " + templateFileName + ".");
                html = fallbackHtml;
            }

            Hashtable reply = new Hashtable();
            reply["int_response_code"] = 200;
            reply["str_response_string"] = html;
            reply["content_type"] = "text/html";
            return reply;
        }

        internal static void debugStringDict(Dictionary<string,string> strs)
        {
            foreach (KeyValuePair<string, string> str in strs)
            {
                m_log.Info("[PayPal] '" + str.Key + "' = '" + str.Value + "'");
            }
        }

        public Hashtable PayPalIPN(Hashtable request)
        {
            Hashtable reply = new Hashtable();

            // Does not matter what we send back to PP here.
            reply["int_response_code"] = 200; // 200 OK
            reply["str_response_string"] = "IPN Processed - Have a nice day.";
            reply["content_type"] = "text/html";

            if (!m_active)
            {
                m_log.Error("[PayPal] Recieved IPN request, but module is disabled. Aborting.");
                reply["str_response_string"] = "IPN Not processed. Module is not enabled.";
                return reply;
            }

            Dictionary<string, string> postvals = new Dictionary<string, string>();
            foreach (KeyValuePair<string, object> kvp in ServerUtils.ParseQueryString((string) request["body"]))
                postvals[kvp.Key] = kvp.Value?.ToString();
            string originalPost = (string) request["body"];

            string modifiedPost = originalPost + "&cmd=_notify-validate";

            HttpWebRequest httpWebRequest = (HttpWebRequest) WebRequest.Create("https://" + m_ppurl + "/cgi-bin/webscr");
            httpWebRequest.Method = "POST";

            httpWebRequest.ContentLength = modifiedPost.Length;
            StreamWriter streamWriter = new StreamWriter(httpWebRequest.GetRequestStream());
            streamWriter.Write(modifiedPost);
            streamWriter.Close();

            string response;

            HttpWebResponse httpWebResponse = (HttpWebResponse) httpWebRequest.GetResponse();
            using (StreamReader streamReader = new StreamReader(httpWebResponse.GetResponseStream()))
            {
                response = streamReader.ReadToEnd();
                streamReader.Close();
            }

            if (httpWebResponse.StatusCode != HttpStatusCode.OK)
            {
                m_log.Error("[PayPal] IPN Status code != 200. Aborting.");
                debugStringDict(postvals);
                return reply;
            }

            if (!response.Contains("VERIFIED"))
            {
                m_log.Error("[PayPal] IPN was NOT verified. Aborting.");
                debugStringDict(postvals);
                return reply;
            }

            // Handle IPN Components
            try
            {
                if (postvals["payment_status"] != "Completed")
                {
                    m_log.Error("[PayPal] Transaction not confirmed. Aborting.");
                    debugStringDict(postvals);
                    return reply;
                }

                if (postvals["mc_currency"].ToUpper() != "USD")
                {
                    m_log.Error("[PayPal] Payment was made in an incorrect currency (" + postvals["mc_currency"] +
                                "). Aborting.");
                    debugStringDict(postvals);
                    return reply;
                }

                // Check we have a transaction with the listed ID.
                UUID txnID = new UUID(postvals["item_number"]);
                PayPalTransaction txn;

                lock (m_transactionsInProgress)
                {
                    if (!m_transactionsInProgress.ContainsKey(txnID))
                    {
                        m_log.Error("[PayPal] Recieved IPN request for Payment that is not in progress. Aborting.");
                        debugStringDict(postvals);
                        return reply;
                    }

                    txn = m_transactionsInProgress[txnID];
                }

                // Verify the payment actually went to the intended seller's PayPal account, not just that SOME
                // payment matching this transaction ID/amount/currency was confirmed. Without this check, a
                // malformed or spoofed IPN naming the correct item_number/mc_gross/mc_currency could still
                // confirm a transaction as paid even if the money was routed to a different PayPal address than
                // the seller actually configured - ported from Mod-PayPal (github.com/SnoopyPfeffer/Mod-PayPal),
                // a real security gap the original 2009-2010 DTL-PayPal never checked.
                if (!postvals.TryGetValue("business", out string businessEmail) ||
                    string.IsNullOrEmpty(businessEmail) ||
                    businessEmail.ToLower() != txn.SellersEmail.ToLower())
                {
                    m_log.Error("[PayPal] IPN 'business' (receiver) email did not match the expected seller (" +
                                txn.SellersEmail + "). Aborting.");
                    debugStringDict(postvals);
                    return reply;
                }

                // Check user paid correctly... epsilon-tolerant rather than exact equality, since decimal
                // rounding on either side of the currency conversion can otherwise cause false-negative aborts
                // on a genuinely correct payment - ported from Mod-PayPal.
                Decimal amountPaid = Decimal.Parse(postvals["mc_gross"]);
                if (Math.Abs(ConvertAmountToCurrency(txn.Amount) - amountPaid) > (Decimal) 0.001)
                {
                    m_log.Error("[PayPal] Expected payment was " + ConvertAmountToCurrency(txn.Amount) +
                                " but recieved " + amountPaid + " " + postvals["mc_currency"] + " instead. Aborting.");
                    debugStringDict(postvals);
                    return reply;
                }

                // At this point, the user has paid, paid a correct amount, in the correct currency.
                // Time to deliver their items. Do it in a seperate thread, so we can return "OK" to PP.
                Util.FireAndForget(delegate { TransferSuccess(txn); });
            }
            catch (KeyNotFoundException)
            {
                m_log.Error("[PayPal] Recieved badly formatted IPN notice. Aborting.");
                debugStringDict(postvals);
                return reply;
            }
            // Wheeeee

            return reply;
        }

        #endregion


        #region Implementation of IRegionModuleBase

        public string Name
        {
            get { return "DeepThink PayPal Module - ©2009 DeepThink Pty Ltd."; }
        }

        public Type ReplaceableInterface
        {
            get { return typeof (IMoneyModule); }
        }

        public void Initialise(IConfigSource source)
        {
            m_log.Info("[PayPal] Initialising.");
            m_config = source;
        }

        public void Close()
        {
            m_active = false;
        }

        public void AddRegion(Scene scene)
        {
            m_log.Info("[PayPal] Found Scene.");

            lock (m_scenes)
                m_scenes.Add(scene);

            if (m_enabled)
                scene.RegisterModuleInterface<IMoneyModule>(this);

            if (m_enabled)
            {
                scene.EventManager.OnMoneyTransfer += EventManager_OnMoneyTransfer;
                scene.EventManager.OnNewClient += EventManager_OnNewClient;
                scene.EventManager.OnMakeRootAgent += MakeRootAgent;
            }
        }

        // BalanceOnEntry (see the m_balanceOnEntry field's doc comment) - ported from Mod-PayPal's own
        // MakeRootAgent()/OnMakeRootAgent hook. Fires whenever an avatar becomes a root agent in this scene, i.e.
        // on login and on every teleport/region-crossing arrival - without this, a balance display can appear to
        // vanish after a teleport until the viewer happens to re-request it on its own.
        private void MakeRootAgent(ScenePresence avatar)
        {
            if (!m_balanceOnEntry)
                return;

            OnMoneyBalanceRequest(avatar.ControllingClient, avatar.UUID, UUID.Zero, UUID.Random());
        }

        #region Basic Plumbing of Currency Events

        void EventManager_OnNewClient(IClientAPI client)
        {
            client.OnMoneyBalanceRequest += OnMoneyBalanceRequest;
            client.OnRequestPayPrice += requestPayPrice;
            client.OnObjectBuy += ObjectBuy;
        }

        internal Scene LocateSceneClientIn(UUID agentID)
        {
            foreach (Scene scene in m_scenes)
            {
                if(scene.Entities.ContainsKey(agentID))
                    return scene;
            }

            return null;
        }

        public void ObjectBuy(IClientAPI remoteClient, UUID agentID,
                UUID sessionID, UUID groupID, UUID categoryID,
                uint localID, byte saleType, int salePrice)
        {
            m_log.Debug("[PayPal] ObjectBuy localID=" + localID + " saleType=" + saleType + " salePrice=" + salePrice + " m_active=" + m_active);

            if (!m_active)
                return;

            IClientAPI user = null;
            Scene scene = null;

            // Find the user's controlling client.
            lock (m_scenes)
            {
                foreach (Scene sc in m_scenes)
                {
                    List<ScenePresence> avs =
                        sc.GetScenePresences().FindAll(
                            x =>
                            (x.UUID == agentID && x.IsChildAgent == false)
                            );

                    if (avs.Count > 0)
                    {
                        if (avs.Count > 1)
                        {
                            m_log.Warn("[PayPal] Multiple avatars with same UUID! Aborting transaction.");
                            return;
                        }

                        // Found the client,
                        // and their root scene.
                        user = avs[0].ControllingClient;
                        scene = sc;
                    }
                }
            }

            if (scene == null || user == null)
            {
                m_log.Warn("[PayPal] Unable to find scene or user! Aborting transaction.");
                return;
            }

            SceneObjectPart sop = scene.GetSceneObjectPart(localID);
            if (sop == null)
            {
                m_log.Warn("[PayPal] Unable to find SceneObjectPart that was paid. Aborting transaction.");
                return;
            }

            if (!TryGetReceiverEmail(sop.OwnerID, sop.GroupID, out string sopEmail))
            {
                m_log.Warn("[PayPal] No PayPal receiver email found for owner " + sop.OwnerID + ". Aborting transaction.");
                return;
            }

            if (IsObjectLocked(sop.UUID))
            {
                m_log.Warn("[PayPal] Object " + sop.UUID + " already has a PayPal transaction in progress. Rejecting.");
                user.SendAlertMessage("This item already has a PayPal payment in progress from another transaction. Please try again shortly.");
                return;
            }

            if (m_minimumAmountCents > 0 && salePrice < m_minimumAmountCents)
            {
                m_log.Warn("[PayPal] Amount " + ConvertAmountToCurrency(salePrice) + " is below the configured minimum of " +
                           ConvertAmountToCurrency(m_minimumAmountCents) + ". Rejecting.");
                user.SendAlertMessage("This amount is too small for a PayPal payment (minimum is " +
                                      ConvertAmountToCurrency(m_minimumAmountCents) + " " + PayPalTransaction.CurrencyCode + ").");
                return;
            }

            PayPalTransaction txn = new PayPalTransaction(agentID, sop.OwnerID, sopEmail, salePrice,
                                                          scene, sop.UUID,
                                                          "Item Purchase - " + sop.Name + " (" + saleType + ")",
                                                          PayPalTransaction.InternalTransactionType.Purchase, categoryID,
                                                          saleType);

            // Add transaction to queue
            lock (m_transactionsInProgress)
                m_transactionsInProgress.Add(txn.TxID, txn);

            string baseUrl = m_scenes[0].RegionInfo.ExternalHostName + ":" + m_scenes[0].RegionInfo.HttpPort;

            user.SendLoadURL("PayPal", txn.ObjectID, txn.To, false, "Confirm purchase?",
                             "http://" + baseUrl + "/dtlpp/?txn=" + txn.TxID);
        }

        public void requestPayPrice(IClientAPI client, UUID objectID)
        {
            Scene scene = LocateSceneClientIn(client.AgentId);
            if (scene == null)
                return;

            SceneObjectPart task = scene.GetSceneObjectPart(objectID);
            if (task == null)
                return;
            SceneObjectGroup group = task.ParentGroup;
            SceneObjectPart root = group.RootPart;

            m_log.Debug("[PayPal] requestPayPrice objectID=" + objectID + " root.PayPrice=[" + string.Join(",", root.PayPrice) + "]");

            client.SendPayPrice(objectID, root.PayPrice);
        }

        static void OnMoneyBalanceRequest(IClientAPI client, UUID agentID, UUID SessionID, UUID TransactionID)
        {
            const int returnfunds = 1000000;
            m_log.Debug("[PayPal] OnMoneyBalanceRequest agentID=" + agentID + " sending balance=" + returnfunds);
            client.SendMoneyBalance(TransactionID, true, Array.Empty<byte>(), returnfunds, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
        }

        #endregion

        public void RemoveRegion(Scene scene)
        {
            lock (m_scenes)
                m_scenes.Remove(scene);

            if (m_enabled)
                scene.EventManager.OnMoneyTransfer -= EventManager_OnMoneyTransfer;
        }

        public void RegionLoaded(Scene scene)
        {
            lock (m_setupLock)
                if (m_setup == false)
                {
                    m_setup = true;
                    FirstRegionLoaded();
                }
        }

        public void PostInitialise()
        {
            IConfig config = m_config.Configs["PayPal"];

            if (null == config)
            {
                m_log.Info("[PayPal] No configuration specified. Skipping.");
                return;
            }

            m_ppurl = config.GetString("PayPalURL", m_ppurl);

            // Determine what the sim's economymodule setting is - mirrors GloebitMoneyModule's own check. Two money
            // modules should never be enabled on the same region: Scene.RegisterModuleInterface<IMoneyModule> is
            // first-registration-wins (see SceneBase.cs), so without this check PayPal and Gloebit could silently
            // race for the slot depending on Mono.Addins region-module load order instead of respecting the admin's
            // configured choice.
            IConfig startupConfig = m_config.Configs["Startup"];
            IConfig economyConfig = m_config.Configs["Economy"];
            string startupEconomyModule = startupConfig?.GetString("economymodule", string.Empty) ?? string.Empty;
            string economyEconomyModule = economyConfig?.GetString("economymodule", string.Empty) ?? string.Empty;

            string economyModule;
            if (string.IsNullOrEmpty(startupEconomyModule) && string.IsNullOrEmpty(economyEconomyModule))
            {
                m_log.Warn("[PayPal] No sim-wide economymodule is set. Defaulting to not-selected.");
                economyModule = string.Empty;
            }
            else if (!string.IsNullOrEmpty(startupEconomyModule) && !string.IsNullOrEmpty(economyEconomyModule))
            {
                if (startupEconomyModule != economyEconomyModule)
                {
                    m_log.Error("[PayPal] economymodule in [Startup] does not match setting in [Economy]. Sim-wide setting is undefined.");
                    economyModule = string.Empty;
                }
                else
                {
                    economyModule = startupEconomyModule;
                }
            }
            else if (!string.IsNullOrEmpty(startupEconomyModule))
            {
                economyModule = startupEconomyModule;
            }
            else
            {
                economyModule = economyEconomyModule;
            }

            if (economyModule != "PayPal")
            {
                m_log.Info("[PayPal] Not selected as sim economymodule. Skipping. (to enable set \"Enabled = true\" in [PayPal] and \"economymodule = PayPal\" in [Economy])");
                return;
            }

            if(!config.GetBoolean("Enabled",false))
            {
                m_log.Info("[PayPal] Not enabled.");
                return;
            }

            m_allowGridEmails = config.GetBoolean("AllowGridEmails", false);
            m_allowGroups = config.GetBoolean("AllowGroups", false);

            // MinimumAmount is configured in dollars (matching PayPalURL/AllowGridEmails' human-readable style);
            // internally everything is tracked in cents (see ConvertAmountToCurrency), so convert once here.
            float minimumAmountDollars = config.GetFloat("MinimumAmount", 0);
            m_minimumAmountCents = (int)Math.Round(minimumAmountDollars * 100);

            m_balanceOnEntry = config.GetBoolean("BalanceOnEntry", false);

            m_log.Warn("[PayPal] Loaded.");


            m_enabled = true;
        }

        public void FirstRegionLoaded()
        {
            IConfig users = m_config.Configs["PayPal Users"];

            if (null == users)
            {
                m_log.Warn("[PayPal] No users specified, skipping load.");
            }
            else
            {
                IUserAccountService userAccountService = m_scenes[0].UserAccountService;
                UUID scopeID = m_scenes[0].RegionInfo.ScopeID;

                // This aborts at the slightest provocation
                // We realise this may be inconvenient for you,
                // however it is important when dealing with
                // financial matters to error check everything.

                foreach (string user in users.GetKeys())
                {
                    UUID tmp;
                    if(UUID.TryParse(user,out tmp))
                    {
                        m_log.Debug("[PayPal] User is UUID, skipping lookup...");
                        string email = users.GetString(user);
                        m_usersemail[tmp] = email;
                        continue;
                    }

                    m_log.Debug("[PayPal] Looking up UUID for " + user);
                    string[] username = user.Split(new[] { ' ' }, 2);
                    UserAccount upd = userAccountService.GetUserAccount(scopeID, username[0], username[1]);

                    if (upd != null)
                    {

                        m_log.Debug("[PayPal] Found, " + user + " = " + upd.PrincipalID);
                        string email = users.GetString(user);

                        if (string.IsNullOrEmpty(email))
                        {
                            m_log.Error("[PayPal] PayPal email address not set for " + user +
                                        " in [PayPal Users] config section. Skipping.");
                            // Did abort here, but since the users are being added to the list regardless...
                        }

                        if (!PayPalHelpers.IsValidEmail(email))
                        {
                            m_log.Error("[PayPal] PayPal email address not valid for " + user +
                                        " in [PayPal Users] config section. Skipping.");
                            // See comment above.
                        }

                        m_usersemail[upd.PrincipalID] = email;
                    }
                    else // UserAccount was null
                    {
                        // Originally aborted the whole FirstRegionLoaded() here (the original author's own comment
                        // above explains why - "it is important when dealing with financial matters to error check
                        // everything") - but that return also skipped AddHTTPHandler("/dtlpp/"...)/m_active=true
                        // below, silently disabling the ENTIRE PayPal module (for every correctly-configured user
                        // too) over one bad name/typo in [PayPal Users]. Skip just this one bad entry instead -
                        // still loud (Error-level log), but no longer takes down payments for everyone else.
                        m_log.Error("[PayPal] Error, User Profile not found for " + user +
                                    ". Check the spelling and/or any associated grid services. Skipping this entry.");
                        continue;
                    }
                }
            }

            if (m_allowGroups)
            {
                IConfig groups = m_config.Configs["PayPal Groups"];

                if (null == groups)
                {
                    m_log.Warn("[PayPal] AllowGroups is enabled but no [PayPal Groups] section found, skipping load.");
                }
                else
                {
                    // Group entries are keyed by group UUID directly (there is no name-based lookup service for
                    // groups the way UserAccountService provides one for avatars), matching how the [PayPal Groups]
                    // section is documented/generated: "GroupUUID=email".
                    foreach (string group in groups.GetKeys())
                    {
                        if (!UUID.TryParse(group, out UUID groupID))
                        {
                            m_log.Error("[PayPal] '" + group + "' in [PayPal Groups] is not a valid group UUID. Skipping.");
                            continue;
                        }

                        string email = groups.GetString(group);

                        if (string.IsNullOrEmpty(email) || !PayPalHelpers.IsValidEmail(email))
                        {
                            m_log.Error("[PayPal] PayPal email address not valid for group " + group +
                                        " in [PayPal Groups] config section. Skipping.");
                            continue;
                        }

                        m_groupsemail[groupID] = email;
                    }
                }
            }

            // Add HTTP Handlers (user, then PP-IPN)
            // BaseHttpServer.CleanSearchPath() unconditionally strips the trailing slash from every incoming
            // request path before matching against m_HTTPHandlers' keys via StartsWith - registering these WITH
            // a trailing slash (the original bug here) meant "/dtlpp".StartsWith("/dtlpp/") was always false,
            // so the handler could never match any real request and every payment confirmation 404'd. Gloebit's
            // own handlers (e.g. "/gloebit/auth_complete") are registered without a trailing slash - match that.
            MainServer.Instance.AddHTTPHandler("/dtlpp", PayPalUserPage);
            MainServer.Instance.AddHTTPHandler("/dtlppipn", PayPalIPN);
            MainServer.Instance.AddHTTPHandler("/dtlpp-return", PayPalReturnPage);
            MainServer.Instance.AddHTTPHandler("/dtlpp-cancel", PayPalCancelPage);

            // XMLRPC Handlers for Standalone
            MainServer.Instance.AddXmlRPCHandler("getCurrencyQuote", quote_func);
            MainServer.Instance.AddXmlRPCHandler("buyCurrency", buy_func);

            m_active = true;
        }

        #endregion

        #region Implementation of IMoneyModule

        public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount, UUID txn, out string reason)
        {
            reason = String.Empty;
            return false; // Objects cant give PP Money. (in theory it's doable however, if the user is in the sim.)
        }

        public event ObjectPaid OnObjectPaid;


        // This will be the maximum amount the user
        // is able to spend due to client limitations.
        // It is set to the equivilent of US$10K
        // as this is PayPal's maximum transaction
        // size.
        //
        // This is 1 Million cents.
        public int GetBalance(UUID agentID)
        {
            return 1000000;
        }

        public int UploadCharge
        {
            get { return 0; }
        }

        public int GroupCreationCharge
        {
            get { return 0; }
        }

        public void ApplyUploadCharge(UUID agentID, int amount, string text)
        {
            // N/A
        }

        public bool UploadCovered(UUID agentID, int amount)
        {
            return true;
        }

        public bool AmountCovered(UUID agentID, int amount)
        {
            return true;
        }

        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData = "")
        {
            // N/A
        }

        public void MoveMoney(UUID fromUser, UUID toUser, int amount, string text)
        {
            // N/A
        }

        public bool MoveMoney(UUID fromUser, UUID toUser, int amount, MoneyTransactionType type, string text)
        {
            return true;
        }

        #endregion

        #region Some Quick Funcs needed for the client

        public XmlRpcResponse quote_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            // Hashtable requestData = (Hashtable) request.Params[0];
            // UUID agentId = UUID.Zero;
            const int amount = 10000;
            Hashtable quoteResponse = new Hashtable();
            XmlRpcResponse returnval = new XmlRpcResponse();


            Hashtable currencyResponse = new Hashtable();
            currencyResponse.Add("estimatedCost", 0);
            currencyResponse.Add("currencyBuy", amount);

            quoteResponse.Add("success", true);
            quoteResponse.Add("currency", currencyResponse);
            quoteResponse.Add("confirm", "asdfad9fj39ma9fj");

            returnval.Value = quoteResponse;
            return returnval;



        }

        public XmlRpcResponse buy_func(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse returnval = new XmlRpcResponse();
            Hashtable returnresp = new Hashtable();
            returnresp.Add("success", true);
            returnval.Value = returnresp;
            return returnval;
        }

        #endregion 

    }
}
