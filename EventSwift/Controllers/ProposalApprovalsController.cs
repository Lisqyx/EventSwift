using EventSwift.Models;
using System;
using System.Data.Entity;
using System.Linq;
using System.Web.Mvc;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using iTextSharp.text;
using iTextSharp.text.pdf;
using System.IO;
using BCrypt.Net;
using PagedList;

namespace EventSwift.Controllers
{
    public class ProposalApprovalsController : BaseController
    {
        private DefaultConnection db = new DefaultConnection();

        // Method to check and update lapsed events (same as in EventProposalsController)
        private void CheckAndUpdateLapsedEvents()
        {
            var now = DateTime.Now;
            var lapsedEvents = db.Events
                .Include(e => e.Proposals)
                .Where(e => e.LapseDate.HasValue &&
                            e.LapseDate < now &&
                            e.Status != "ApprovedByPresident" &&
                            e.Status != "Lapsed")
                .ToList();

            foreach (var ev in lapsedEvents)
            {
                ev.Status = "Lapsed";

                foreach (var proposal in ev.Proposals)
                {
                    if (proposal.Status != "Approved")
                    {
                        proposal.Status = "Lapsed";

                        var approvals = db.ProposalApprovals.Where(a => a.EventProposalId == proposal.EventProposalId).ToList();
                        foreach (var approval in approvals)
                        {
                            if (approval.Status == "Pending")
                            {
                                approval.Status = "Lapsed";
                            }
                        }
                    }
                }

                var client = db.Users.FirstOrDefault(u => u.UserId == ev.ClientId);
                if (client != null)
                {
                    db.Notifications.Add(new Notification
                    {
                        Username = client.Username,
                        Message = $"Your event '{ev.Title}' has lapsed as it was not approved by {ev.LapseDate?.ToString("MMM dd, yyyy hh:mm tt")}.",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });
                }
            }

            if (lapsedEvents.Any())
            {
                db.SaveChanges();
            }
        }

        public class OfficeEventProposalsVM
        {
            public Event Event { get; set; }
            public List<EventProposal> Proposals { get; set; }
        }

        public ActionResult ApprovalsIndex(int? page)
        {
            // Check for lapsed events
            CheckAndUpdateLapsedEvents();

            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
            if (currentUser == null)
                return HttpNotFound("User not found");

            var officeRole = currentUser.Role;

            int pageSize = 5;
            int pageNumber = page ?? 1;

            var events = db.Events
                .Include(e => e.Proposals.Select(p => p.Approvals))
                .Where(ev => ev.Proposals.Any(p => p.Approvals.Any(a => a.Office == officeRole)))
                .OrderByDescending(e => e.EventId)
                .ToPagedList(pageNumber, pageSize);

            return View(events);
        }

        public ActionResult EventDetails(int id)
        {
            // Check for lapsed events
            CheckAndUpdateLapsedEvents();

            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
            if (currentUser == null)
                return HttpNotFound("User not found");

            var officeRole = currentUser.Role;

            var ev = db.Events
                .Include(e => e.Proposals.Select(p => p.Approvals))
                .FirstOrDefault(e => e.EventId == id);
            if (ev == null)
                return HttpNotFound();

            var proposals = ev.Proposals
                .Where(p => p.Approvals.Any(a => a.Office == officeRole))
                .ToList();

            ViewBag.Role = officeRole;
            ViewBag.EventTitle = ev.Title;
            ViewBag.EventStatus = ev.Status;
            ViewBag.EventId = ev.EventId;
            ViewBag.IsEventLapsed = ev.Status == "Lapsed" || ev.IsLapsed;
            ViewBag.LapseDate = ev.LapseDate;

            return View(proposals);
        }

        public ActionResult VPAEventOverview(int id)
        {
            // Check for lapsed events
            CheckAndUpdateLapsedEvents();

            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
            if (currentUser == null || currentUser.Role != "VPA")
                return HttpNotFound("Access denied. VPA access only.");

            var ev = db.Events
                .Include(e => e.Proposals.Select(p => p.Approvals))
                .FirstOrDefault(e => e.EventId == id);
            if (ev == null)
                return HttpNotFound();

            ViewBag.IsEventLapsed = ev.Status == "Lapsed" || ev.IsLapsed;

            return View(ev);
        }

        public ActionResult Details(int id)
        {
            // Check for lapsed events
            CheckAndUpdateLapsedEvents();

            var approval = db.ProposalApprovals
                             .Include("EventProposal")
                             .Include("EventProposal.Event")
                             .FirstOrDefault(a => a.ProposalApprovalId == id);

            if (approval == null)
                return HttpNotFound();

            ViewBag.EventId = approval.EventProposal?.EventId ?? 0;
            ViewBag.Role = approval.Office;
            ViewBag.IsEventLapsed = approval.EventProposal?.Event?.Status == "Lapsed" ||
                                    (approval.EventProposal?.Event?.IsLapsed ?? false);
            ViewBag.LapseDate = approval.EventProposal?.Event?.LapseDate;

            return View(approval);
        }

        public ActionResult Action(int id)
        {
            var approval = db.ProposalApprovals
                .Include("EventProposal.Event")
                .FirstOrDefault(a => a.ProposalApprovalId == id);
            if (approval == null)
                return HttpNotFound();

            // Check if event is lapsed
            if (approval.EventProposal?.Event?.Status == "Lapsed" ||
                (approval.EventProposal?.Event?.IsLapsed ?? false))
            {
                TempData["Error"] = "Cannot take action. This event has lapsed.";
                return RedirectToAction("Details", new { id = id });
            }

            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
            ViewBag.CurrentUser = currentUser;

            return View(approval);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Action(int id, string action, string feedbackMessage, DateTime? eventDate, string eventVenue, string approvalPassword, string signatureData)
        {
            var approval = db.ProposalApprovals
                   .Include(a => a.EventProposal)
                   .Include(a => a.EventProposal.Client)
                   .Include(a => a.EventProposal.Event)
                   .FirstOrDefault(a => a.ProposalApprovalId == id);

            if (approval == null)
                return HttpNotFound();

            // Check if event is lapsed
            if (approval.EventProposal?.Event?.Status == "Lapsed" ||
                (approval.EventProposal?.Event?.IsLapsed ?? false))
            {
                if (Request.IsAjaxRequest())
                {
                    return Json(new { success = false, message = "Cannot take action. This event has lapsed." });
                }
                TempData["Error"] = "Cannot take action. This event has lapsed.";
                return RedirectToAction("Details", new { id = id });
            }

            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
            if (currentUser == null)
            {
                ModelState.AddModelError("", "User not found.");
                ViewBag.CurrentUser = null;
                if (Request.IsAjaxRequest())
                {
                    return Json(new { success = false, message = "User not found." });
                }
                return View(approval);
            }

            if (action == "Approve")
            {
                // Validate password
                if (string.IsNullOrEmpty(approvalPassword) || !BCrypt.Net.BCrypt.Verify(approvalPassword, currentUser.PasswordHash))
                {
                    ModelState.AddModelError("approvalPassword", "Incorrect password.");
                    ViewBag.CurrentUser = currentUser;
                    if (Request.IsAjaxRequest())
                    {
                        return Json(new { success = false, message = "Incorrect password." });
                    }
                    return View(approval);
                }
                // Validate signature
                if (string.IsNullOrEmpty(signatureData) || !signatureData.StartsWith("data:image/png;base64,"))
                {
                    ModelState.AddModelError("signatureData", "Signature is required.");
                    ViewBag.CurrentUser = currentUser;
                    if (Request.IsAjaxRequest())
                    {
                        return Json(new { success = false, message = "Signature is required." });
                    }
                    return View(approval);
                }

                var base64 = signatureData.Substring("data:image/png;base64,".Length);
                byte[] imageBytes = Convert.FromBase64String(base64);
                string tempSignaturePath = Path.GetTempFileName() + ".png";
                System.IO.File.WriteAllBytes(tempSignaturePath, imageBytes);

                approval.Status = "Approved";
                approval.ActionDate = DateTime.Now;

                if (approval.Office == "VPA")
                {
                    approval.EventProposal.Status = "Approved";
                }
                else
                {
                    approval.EventProposal.Status = "Reviewed";
                }

                if (approval.Office == "VPAA" || approval.Office == "VPF" || approval.Office == "AMU")
                {
                    var vpaApproval = new ProposalApproval
                    {
                        EventProposalId = approval.EventProposalId,
                        Office = "VPA",
                        Status = "Pending",
                        ActionDate = null
                    };
                    db.ProposalApprovals.Add(vpaApproval);

                    var vpaUsers = db.Users.Where(u => u.Role == "VPA").ToList();
                    foreach (var user in vpaUsers)
                    {
                        db.Notifications.Add(new Notification
                        {
                            Username = user.Username,
                            Message = $"A document '{approval.EventProposal.Title}' has been approved by {approval.Office} and forwarded to you for review.",
                            IsRead = false,
                            CreatedAt = DateTime.Now
                        });
                    }
                }

                if (approval.Office == "VPA" && eventDate.HasValue)
                {
                    approval.EventProposal.Event.ApprovedDate = eventDate.Value;
                    if (!string.IsNullOrEmpty(eventVenue))
                    {
                        approval.EventProposal.Event.Venue = eventVenue;
                    }
                }

                var filePath = approval.EventProposal.FilePath;
                if (!string.IsNullOrEmpty(filePath) && Path.GetExtension(filePath).ToLower() == ".pdf")
                {
                    string pdfPath = Server.MapPath(filePath);
                    if (System.IO.File.Exists(pdfPath))
                    {
                        int stackIndex = 0;
                        if (approval.Office == "VPA")
                        {
                            var previousApproval = db.ProposalApprovals
                                .Where(a => a.EventProposalId == approval.EventProposalId &&
                                            (a.Office == "VPF" || a.Office == "VPAA" || a.Office == "AMU") &&
                                            a.Status == "Approved")
                                .OrderByDescending(a => a.ActionDate)
                                .FirstOrDefault();
                            if (previousApproval != null)
                            {
                                stackIndex = 1;
                            }
                        }
                        StampPdfWithSignature(pdfPath, tempSignaturePath, approval.Office, stackIndex);
                    }
                }

                if (System.IO.File.Exists(tempSignaturePath))
                {
                    System.IO.File.Delete(tempSignaturePath);
                }

                db.Notifications.Add(new Notification
                {
                    Username = approval.EventProposal.Client.Username,
                    Message = $"Your proposal '{approval.EventProposal.Title}' has been approved by {approval.Office}.",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }
            else if (action == "Reject")
            {
                if (string.IsNullOrWhiteSpace(feedbackMessage))
                {
                    ModelState.AddModelError("feedbackMessage", "Feedback message is required when rejecting.");
                    ViewBag.CurrentUser = currentUser;
                    if (Request.IsAjaxRequest())
                    {
                        return Json(new { success = false, message = "Feedback message is required when rejecting." });
                    }
                    return View(approval);
                }

                approval.Status = "Rejected";
                approval.ActionDate = DateTime.Now;
                approval.EventProposal.Status = "Rejected";

                var feedback = new ProposalFeedback
                {
                    EventProposalId = approval.EventProposalId,
                    Office = approval.Office,
                    Feedback = feedbackMessage,
                    FeedbackDate = DateTime.Now,
                    SubmittedAt = DateTime.Now
                };
                db.ProposalFeedbacks.Add(feedback);

                db.Notifications.Add(new Notification
                {
                    Username = approval.EventProposal.Client.Username,
                    Message = $"Your proposal '{approval.EventProposal.Title}' has been rejected by {approval.Office}.",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }
            else
            {
                ModelState.AddModelError("", "Invalid action.");
                ViewBag.CurrentUser = currentUser;
                if (Request.IsAjaxRequest())
                {
                    return Json(new { success = false, message = "Invalid action." });
                }
                return View(approval);
            }

            db.SaveChanges();

            if (Request.IsAjaxRequest())
            {
                string msg = action == "Approve"
                    ? "Proposal approved successfully."
                    : action == "Reject"
                        ? "Proposal returned with feedback."
                        : "Action completed.";
                return Json(new { success = true, message = msg });
            }
            return RedirectToAction("ApprovalsIndex");
        }

        // ... rest of your existing methods (StampPdfWithSignature, SendToOtherOffice, DeleteEvent) ...

        // Keep all your existing helper methods
        private void StampPdfWithSignature(string pdfPath, string signatureImagePath, string office, int stackIndex = 0)
        {
            // Your existing implementation
            string tempPath = pdfPath + ".tmp";
            using (var reader = new PdfReader(pdfPath))
            using (var stamper = new PdfStamper(reader, new FileStream(tempPath, FileMode.Create)))
            {
                int pageCount = reader.NumberOfPages;
                iTextSharp.text.Image signature = iTextSharp.text.Image.GetInstance(signatureImagePath);
                signature.ScaleAbsolute(120, 50);

                PdfContentByte over = stamper.GetOverContent(pageCount);

                var pageSize = reader.GetPageSize(pageCount);
                float pageWidth = pageSize.Width;

                float marginRight = 50f;
                float marginBottom = 30f;
                float signatureW = 120f;
                float signatureH = 50f;
                float textBlockH = 10f;
                float spacing = 0f;
                float stackGap = 0f;
                float xPos = pageWidth - marginRight - signatureW;
                float unitH = textBlockH + spacing + signatureH;
                float baseY = marginBottom + (stackIndex * (unitH + stackGap));

                BaseColor textColor = BaseColor.BLACK;
                BaseColor violet = new BaseColor(62, 30, 130);

                switch (office.ToUpper())
                {
                    case "VPAA":
                    case "VPF":
                    case "AMU":
                    case "VPA":
                        textColor = violet;
                        break;
                }

                BaseFont bf = BaseFont.CreateFont(BaseFont.HELVETICA_BOLD, BaseFont.CP1252, BaseFont.NOT_EMBEDDED);

                float textY1 = baseY + 15f;
                float textY2 = baseY + 0f;

                over.BeginText();
                over.SetFontAndSize(bf, 12);
                over.SetColorFill(textColor);

                string approvalText = $"APPROVED BY {office}";
                over.ShowTextAligned(Element.ALIGN_LEFT, approvalText, xPos, textY1, 0);

                string dateText = DateTime.Now.ToString("MMM dd, yyyy");
                over.ShowTextAligned(Element.ALIGN_LEFT, dateText, xPos, textY2, 0);

                over.EndText();

                float signatureY = baseY + textBlockH + spacing;
                signature.SetAbsolutePosition(xPos, signatureY);
                over.AddImage(signature);

                stamper.Close();
            }

            System.IO.File.Delete(pdfPath);
            System.IO.File.Move(tempPath, pdfPath);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult SendToOtherOffice(int proposalApprovalId, string targetOffice)
        {
            var approval = db.ProposalApprovals
                             .Include(a => a.EventProposal.Client)
                             .Include(a => a.EventProposal.Event)
                             .FirstOrDefault(a => a.ProposalApprovalId == proposalApprovalId);

            if (approval == null)
                return HttpNotFound();

            // Check if event is lapsed
            if (approval.EventProposal?.Event?.Status == "Lapsed" ||
                (approval.EventProposal?.Event?.IsLapsed ?? false))
            {
                TempData["Error"] = "Cannot forward document. This event has lapsed.";
                return RedirectToAction("ApprovalsIndex");
            }

            approval.Office = targetOffice;
            approval.Status = "Pending";
            approval.ActionDate = null;

            var officeUsers = db.Users.Where(u => u.Role == targetOffice).ToList();
            foreach (var user in officeUsers)
            {
                db.Notifications.Add(new Notification
                {
                    Username = user.Username,
                    Message = $"A document titled '{approval.EventProposal.Title}' has been forwarded to your office.",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            db.SaveChanges();

            TempData["Message"] = $"Document sent to {targetOffice}.";
            return RedirectToAction("ApprovalsIndex");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteEvent(int eventId)
        {
            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
            if (currentUser == null)
                return HttpNotFound("User not found");

            var officeRole = currentUser.Role;

            var ev = db.Events
                .Include(e => e.Proposals.Select(p => p.Approvals))
                .Include(e => e.Client)
                .FirstOrDefault(e => e.EventId == eventId);

            if (ev == null)
            {
                TempData["Error"] = "Event not found.";
                return RedirectToAction("ApprovalsIndex");
            }

            var officeProposals = ev.Proposals.Where(p => p.Approvals.Any(a => a.Office == officeRole)).ToList();
            if (!officeProposals.Any())
            {
                TempData["Error"] = "You don't have permission to delete this event.";
                return RedirectToAction("ApprovalsIndex");
            }

            foreach (var proposal in officeProposals)
            {
                if (!string.IsNullOrEmpty(proposal.FilePath))
                {
                    string fullPath = Server.MapPath(proposal.FilePath);
                    if (System.IO.File.Exists(fullPath))
                    {
                        System.IO.File.Delete(fullPath);
                    }
                }

                var feedbacks = db.ProposalFeedbacks.Where(f => f.EventProposalId == proposal.EventProposalId).ToList();
                db.ProposalFeedbacks.RemoveRange(feedbacks);

                var approvals = db.ProposalApprovals.Where(a => a.EventProposalId == proposal.EventProposalId).ToList();
                db.ProposalApprovals.RemoveRange(approvals);

                db.EventProposals.Remove(proposal);
            }

            if (ev.Client != null)
            {
                db.Notifications.Add(new Notification
                {
                    Username = ev.Client.Username,
                    Message = $"Your event '{ev.Title}' has been deleted by {officeRole}.",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            db.SaveChanges();

            TempData["Success"] = "Event and associated documents deleted successfully.";
            return RedirectToAction("ApprovalsIndex");
        }
    }
}