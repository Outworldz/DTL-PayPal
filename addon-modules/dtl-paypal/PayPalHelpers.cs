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
using System.Text.RegularExpressions;

namespace DeepThink.PayPal
{
    public static class PayPalHelpers
    {
        /// <summary>
        /// Whether this is a properly formed email address. It checks the form only; PayPal itself decides whether
        /// the account exists.
        ///
        /// The work is done by the EmailValidation library (NuGet package "EmailValidation" by Jeffrey Stedfast,
        /// MIT licence), which follows the mail standard, RFC 5322. EmailValidation.dll must sit in bin beside
        /// PayPal.dll.
        ///
        /// The hand-written pattern this replaces allowed only letters, digits, dots and hyphens before the @, and
        /// only a short fixed list of endings (com, net, org ...) or any two letters. It turned away ordinary
        /// addresses such as deb_scott@outlook.com (underscore), name+tag@gmail.com (plus sign) and anything at
        /// .live, .online, .app or .shop.
        /// </summary>
        /// <param name="email">email address to validate</param>
        /// <returns>true is valid, false if not valid</returns>
        public static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            // allowTopLevelDomains false: "someone@localhost" is refused, the part after the @ needs a dot.
            // allowInternational false: plain ASCII addresses only.
            return EmailValidation.EmailValidator.Validate(email, false, false);
        }
    }
}