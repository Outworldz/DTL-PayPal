A currency module for OpenSimulator (www.opensimulator.org)

THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY

EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED

WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE

DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;

LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT

(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS

SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

----
WARNING: USING THIS IN ANY FORM OF PRODUCTION ENVIRONMENT IS ASKING FOR TROUBLE. 
IT IS HIGHLY UNTESTED, EXPERIMENTAL AND COMPLETELY UNWARRANTIED. 
IF IT BREAKS, YOU GET TO KEEP BOTH PIECES, BUT MAY HAVE LOST YOUR WALLET.
----

This module uses PayPal as the backend, this means all transactions are real 
hard transactions in USD. By default, this module sets the rate at 
OS$1 = 1 US Cent, eg - OS$300 = US$3.00. Payments are confirmed by the buyer 
through PayPal's standard purchasing interface; the simulator is then notified 
via PayPal IPN of the payment - and will process the original request.


Configuring this module:
Add the following sections to your OpenSim.ini file

[Economy] (or [Startup], not both - see below)
economymodule = PayPal

[PayPal]
Enabled = true
AllowGridEmails = false
AllowGroups = false
; Optional: reject a payment/purchase up front (with a clear message) if it's below this many US dollars,
; instead of letting the buyer hit a confusing error on PayPal's own checkout page for amounts too small to
; cover PayPal's transaction fees. 0 (the default) means no minimum is enforced.
MinimumAmount = 0
; Re-send the avatar's balance every time they become a root agent (login, teleport, region crossing).
; Without this, a balance display can appear to vanish after a teleport until the viewer happens to
; re-request it on its own. Default is off.
BalanceOnEntry = false

[PayPal Users]
User Name=paypal@email.com
Other User=another.paypal@email.com
...
etc
...

[PayPal Groups]
a683cc8a-a5cc-4c40-87bc-ebcfbcfb1456=mygroup.account@yahoo.com
...
etc
...

How the PayPal Users section is formatted:
One line per user, this should be the avatar name. The email address is the
primary email address used for the PayPal account that their funds will be
recieved by. Users not listed in this section will not be able to recieve
funds (however any user can send them.) If AllowGridEmails is true, a user's
own grid profile email is used as a fallback when they have no explicit entry
here.

How the PayPal Groups section is formatted (only used when AllowGroups is
true): one line per group, keyed by the group's UUID, mapping to the PayPal
receiver email that group-owned objects pay out to.

Selecting PayPal as the active currency module:
Only one money module should ever be active on a given region (e.g. PayPal,
Gloebit, or the free BetaGridLikeMoneyModule) - if more than one registers
itself, OpenSim uses whichever happens to load first, which is not something
you want to leave to chance. To make sure PayPal is the one that wins, set
"economymodule = PayPal" in either [Startup] or [Economy] (not both - if
they disagree, neither module will consider itself selected). PayPal will
only activate when BOTH "economymodule = PayPal" AND "[PayPal] Enabled =
true" are set; if you only set [PayPal] Enabled and leave economymodule
unset or pointing elsewhere, PayPal will log that it was not selected and
stay inactive.

Transaction locking:
While an object has a PayPal payment/purchase in progress (buyer sent to
PayPal's checkout, IPN not yet received), a second buy/pay attempt on that
same object is rejected with an in-viewer message rather than starting a
second concurrent transaction. If a buyer never completes PayPal's checkout,
the lock is automatically released after 5 minutes.