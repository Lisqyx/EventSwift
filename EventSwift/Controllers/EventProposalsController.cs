using EventSwift.Models;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using PagedList;

namespace EventSwift.Controllers
{
    public class EventProposalsController : BaseController
    {
        private DefaultConnection db = new DefaultConnection();

        // Method to check and update lapsed EVENTS (not proposals)
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

                // Mark all proposals under this event as Lapsed
                foreach (var proposal in ev.Proposals)
                {
                    if (proposal.Status != "Approved")
                    {
                        proposal.Status = "Lapsed";

                        // Update related approvals
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

                // Notify the client
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

        // This is the automatic follow up when yo event bout to go down
        private void SendAutoFollowUpForNearLapseEvents()
        {
            var now = DateTime.Now;
            var warningThreshold = now.AddDays(1); // 1 day before lapse (can edit)

            var nearLapseEvents = db.Events
                .Include(e => e.Proposals)
                .Where(e => e.LapseDate.HasValue &&
                            e.LapseDate > now &&
                            e.LapseDate <= warningThreshold &&
                            e.Status != "ApprovedByPresident" &&
                            e.Status != "Lapsed")
                .ToList();

            foreach (var ev in nearLapseEvents)
            {
                // Check pending proposals
                var pendingProposals = ev.Proposals.Where(p => p.Status == "Pending" || p.Status == "Reviewed").ToList();

                foreach (var proposal in pendingProposals)
                {
                    // Check if auto follow-up was already sent today
                    var alreadySent = db.Notifications
                        .Any(n => n.Message.Contains("AUTO FOLLOW-UP") &&
                                  n.Message.Contains(proposal.Title) &&
                                  n.CreatedAt >= now.Date);

                    if (!alreadySent)
                    {
                        var officeUsers = db.Users.Where(u => u.Role == proposal.TargetOfficeRole).ToList();
                        foreach (var user in officeUsers)
                        {
                            db.Notifications.Add(new Notification
                            {
                                Username = user.Username,
                                Message = $"[AUTO FOLLOW-UP] URGENT: The event '{ev.Title}' is about to lapse on {ev.LapseDate?.ToString("MMM dd, yyyy hh:mm tt")}. Please review the proposal '{proposal.Title}' immediately.",
                                IsRead = false,
                                CreatedAt = DateTime.Now
                            });
                        }
                    }
                }
            }

            db.SaveChanges();
        }

        // GET: EventProposals
        public ActionResult Index(int? page)
        {
            // Check for lapsed events and send auto follow-ups
            CheckAndUpdateLapsedEvents();
            SendAutoFollowUpForNearLapseEvents();

            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
            if (currentUser == null) return HttpNotFound();

            int pageSize = 5;
            int pageNumber = page ?? 1;

            var events = db.Events
                           .Where(e => e.ClientId == currentUser.UserId)
                           .OrderByDescending(e => e.EventId)
                           .ToPagedList(pageNumber, pageSize);

            return View(events);
        }

        public ActionResult CreateEvent()
        {
            return View();
        }

        // POST: EventProposals/CreateEvent
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult CreateEvent(CreateEventViewModel model)
        {
            if (ModelState.IsValid)
            {
                var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
                if (currentUser == null)
                {
                    ModelState.AddModelError("", "User not found.");
                    return View(model);
                }

                // Create the event with LapseDate
                var eventItem = new Event
                {
                    Title = model.Title,
                    Status = "Pending",
                    ClientId = currentUser.UserId,
                    CreatedAt = DateTime.Now,
                    LapseDate = model.LapseDate // Set lapse date on event
                };

                db.Events.Add(eventItem);
                db.SaveChanges();

                // Process file uploads for each office
                var offices = new[] { "VPAA", "VPF", "AMU", "VPA" };
                var fileProperties = new[] { model.VPAAFile, model.VPFFile, model.AMUFile, model.VPAFile };

                for (int i = 0; i < offices.Length; i++)
                {
                    var file = fileProperties[i];
                    var office = offices[i];

                    if (file != null && file.ContentLength > 0)
                    {
                        string fileExtension = Path.GetExtension(file.FileName).ToLower();
                        if (fileExtension != ".pdf")
                        {
                            ModelState.AddModelError("", $"Only PDF files are allowed for {office} office.");
                            return View(model);
                        }

                        string uploadFolder = Server.MapPath("~/Uploads");
                        if (!Directory.Exists(uploadFolder))
                        {
                            Directory.CreateDirectory(uploadFolder);
                        }

                        string originalFileName = Path.GetFileName(file.FileName);
                        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
                        string fileExt = Path.GetExtension(originalFileName);

                        string uniqueFileName = $"{fileNameWithoutExt}_{office}_{eventItem.EventId}_{DateTime.Now:yyyyMMddHHmmss}{fileExt}";
                        string path = Path.Combine(uploadFolder, uniqueFileName);
                        file.SaveAs(path);

                        // Create proposal WITHOUT LapseDate (it's on the event now)
                        var proposal = new EventProposal
                        {
                            Title = fileNameWithoutExt,
                            FilePath = "/Uploads/" + uniqueFileName,
                            Status = "Pending",
                            EventId = eventItem.EventId,
                            ClientId = currentUser.UserId,
                            TargetOfficeRole = office,
                            SubmittedAt = DateTime.Now
                        };

                        db.EventProposals.Add(proposal);
                        db.SaveChanges();

                        var approval = new ProposalApproval
                        {
                            EventProposalId = proposal.EventProposalId,
                            Office = office,
                            Status = "Pending",
                            ActionDate = null
                        };

                        db.ProposalApprovals.Add(approval);

                        var officeUsers = db.Users.Where(u => u.Role == office).ToList();
                        foreach (var user in officeUsers)
                        {
                            var lapseDateText = model.LapseDate.HasValue
                                ? $" Event Lapse Date: {model.LapseDate.Value:MMM dd, yyyy hh:mm tt}"
                                : "";
                            var notification = new Notification
                            {
                                Username = user.Username,
                                Message = $"A new proposal titled '{proposal.Title}' for event '{eventItem.Title}' has been submitted.{lapseDateText}",
                                IsRead = false,
                                CreatedAt = DateTime.Now
                            };
                            db.Notifications.Add(notification);
                        }
                    }
                }

                db.SaveChanges();
                return RedirectToAction("Details", new { id = eventItem.EventId });
            }

            return View(model);
        }

        // GET: EventProposals/Create
        public ActionResult Create()
        {
            return View();
        }

        // POST: EventProposals/Create (for individual documents - no lapseDate parameter)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Create(EventProposal proposal, HttpPostedFileBase uploadedFile, int eventId, string targetOffice)
        {
            using (var db = new DefaultConnection())
            {
                var eventItem = db.Events.FirstOrDefault(e => e.EventId == eventId);

                // Check if event is lapsed
                if (eventItem != null && (eventItem.Status == "Lapsed" || eventItem.IsLapsed))
                {
                    TempData["Error"] = "Cannot create new documents. This event has lapsed.";
                    return RedirectToAction("Details", new { id = eventId });
                }

                if (eventItem != null && (eventItem.Status == "SentToPresident" || eventItem.Status == "ApprovedByPresident"))
                {
                    TempData["Error"] = "Cannot create new documents. This event has been sent to the President for approval.";
                    return RedirectToAction("Details", new { id = eventId });
                }

                if (uploadedFile != null && uploadedFile.ContentLength > 0)
                {
                    string fileExtension = Path.GetExtension(uploadedFile.FileName).ToLower();

                    if (fileExtension != ".pdf")
                    {
                        ModelState.AddModelError("uploadedFile", "Only PDF files are allowed.");
                        return RedirectToAction("Details", new { id = eventId });
                    }

                    string uploadFolder = Server.MapPath("~/Uploads");
                    if (!Directory.Exists(uploadFolder))
                    {
                        Directory.CreateDirectory(uploadFolder);
                    }

                    string originalFileName = Path.GetFileName(uploadedFile.FileName);
                    string fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
                    string fileExt = Path.GetExtension(originalFileName);

                    string uniqueFileName = $"{fileNameWithoutExt}_{eventId}_{DateTime.Now:yyyyMMddHHmmss}{fileExt}";
                    string path = Path.Combine(uploadFolder, uniqueFileName);
                    uploadedFile.SaveAs(path);
                    proposal.FilePath = "/Uploads/" + uniqueFileName;
                }
                else
                {
                    ModelState.AddModelError("uploadedFile", "Please insert a file to upload.");
                    return RedirectToAction("Details", new { id = eventId });
                }

                proposal.TargetOfficeRole = targetOffice;
                proposal.EventId = eventId;
                proposal.Status = "Pending";
                proposal.SubmittedAt = DateTime.Now;

                var client = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
                if (client == null)
                {
                    ModelState.AddModelError("", "User not found.");
                    return RedirectToAction("Details", new { id = eventId });
                }
                proposal.ClientId = client.UserId;

                db.EventProposals.Add(proposal);
                db.SaveChanges();

                if (!string.IsNullOrEmpty(proposal.TargetOfficeRole))
                {
                    var approval = new ProposalApproval
                    {
                        EventProposalId = proposal.EventProposalId,
                        Office = proposal.TargetOfficeRole,
                        Status = "Pending",
                        ActionDate = null
                    };

                    db.ProposalApprovals.Add(approval);

                    var officeUsers = db.Users.Where(u => u.Role == proposal.TargetOfficeRole).ToList();

                    foreach (var user in officeUsers)
                    {
                        var lapseDateText = eventItem?.LapseDate.HasValue == true
                            ? $" Event Lapse Date: {eventItem.LapseDate.Value:MMM dd, yyyy hh:mm tt}"
                            : "";
                        var notification = new Notification
                        {
                            Username = user.Username,
                            Message = $"A new proposal titled '{proposal.Title}' for event '{eventItem?.Title}' has been submitted.{lapseDateText}",
                            IsRead = false,
                            CreatedAt = DateTime.Now
                        };
                        db.Notifications.Add(notification);
                    }

                    db.SaveChanges();
                }

                return RedirectToAction("Details", new { id = eventId });
            }
        }

        // GET: EventProposals/Details/5
        public ActionResult Details(int id)
        {
            // Check for lapsed events and send auto follow-ups
            CheckAndUpdateLapsedEvents();
            SendAutoFollowUpForNearLapseEvents();

            var ev = db.Events
                .Include(e => e.Proposals.Select(p => p.Approvals))
                .FirstOrDefault(e => e.EventId == id);
            if (ev == null) return HttpNotFound();

            return View(ev);
        }

        // ... rest of your existing controller methods stay the same ...
        // Just update Edit, Resubmit, FollowUpAjax to check event lapse instead of proposal lapse

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult SendToPresident(int eventId)
        {
            var ev = db.Events
                .Include(e => e.Proposals.Select(p => p.Approvals))
                .FirstOrDefault(e => e.EventId == eventId);

            if (ev == null) return HttpNotFound();

            // Check if event is lapsed
            if (ev.Status == "Lapsed" || ev.IsLapsed)
            {
                TempData["Error"] = "Cannot send to President. This event has lapsed.";
                return RedirectToAction("Details", new { id = eventId });
            }

            var vpaApprovals = ev.Proposals.SelectMany(p => p.Approvals).Where(a => a.Office == "VPA").ToList();
            bool allVPAApproved = vpaApprovals.Any() && vpaApprovals.All(a => a.Status == "Approved");

            if (!allVPAApproved)
            {
                TempData["Error"] = "Cannot send to President. All documents must be approved by VPA first.";
                return RedirectToAction("Details", new { id = eventId });
            }

            ev.Status = "SentToPresident";
            db.SaveChanges();

            var presidents = db.Users.Where(u => u.Role == "President").ToList();
            foreach (var pres in presidents)
            {
                db.Notifications.Add(new Notification
                {
                    Username = pres.Username,
                    Message = $"Event '{ev.Title}' has been sent for your final approval.",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }
            db.SaveChanges();

            TempData["Success"] = "Event sent to the President successfully.";
            return RedirectToAction("Details", new { id = eventId });
        }

        public ActionResult DetailProposal(int id)
        {
            var proposal = db.EventProposals
                .Include(p => p.Feedbacks)
                .Include(p => p.Event)
                .FirstOrDefault(p => p.EventProposalId == id);

            if (proposal == null)
            {
                return HttpNotFound();
            }

            if (proposal.Feedbacks == null)
            {
                proposal.Feedbacks = new List<ProposalFeedback>();
            }

            return View(proposal);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Delete(int id)
        {
            var proposal = db.EventProposals.Include(p => p.Event).FirstOrDefault(p => p.EventProposalId == id);
            if (proposal == null)
            {
                return HttpNotFound();
            }

            // Check if event is lapsed
            if (proposal.Event.Status == "Lapsed" || proposal.Event.IsLapsed)
            {
                TempData["Error"] = "Cannot delete documents from a lapsed event.";
                return RedirectToAction("Details", new { id = proposal.EventId });
            }

            string fullPath = Server.MapPath(proposal.FilePath);
            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }

            db.EventProposals.Remove(proposal);
            db.SaveChanges();

            return RedirectToAction("Details", new { id = proposal.EventId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteEvent(int eventId)
        {
            var ev = db.Events.Include(e => e.Proposals).FirstOrDefault(e => e.EventId == eventId);

            if (ev == null)
            {
                TempData["Error"] = "Event not found.";
                return RedirectToAction("Index");
            }

            foreach (var proposal in ev.Proposals.ToList())
            {
                string fullPath = Server.MapPath(proposal.FilePath);
                if (System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                }

                var approvals = db.ProposalApprovals.Where(a => a.EventProposalId == proposal.EventProposalId).ToList();
                db.ProposalApprovals.RemoveRange(approvals);

                db.EventProposals.Remove(proposal);
            }

            db.Events.Remove(ev);
            db.SaveChanges();

            return RedirectToAction("Index");
        }

        public ActionResult Edit(int id)
        {
            var proposal = db.EventProposals.Include(p => p.Event).FirstOrDefault(p => p.EventProposalId == id);
            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);

            if (proposal == null || proposal.ClientId != currentUser.UserId)
                return HttpNotFound();

            // Check if event is lapsed
            if (proposal.Event.Status == "Lapsed" || proposal.Event.IsLapsed)
            {
                TempData["Error"] = "Cannot edit documents. This event has lapsed.";
                return RedirectToAction("Details", new { id = proposal.EventId });
            }

            if (proposal.Status != "Pending")
            {
                TempData["Error"] = "Only pending documents can be edited.";
                return RedirectToAction("Details", new { id = proposal.EventId });
            }

            return View(proposal);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit(int id, HttpPostedFileBase uploadedFile)
        {
            var proposal = db.EventProposals.Include(p => p.Event).FirstOrDefault(p => p.EventProposalId == id);
            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);

            if (proposal == null || proposal.ClientId != currentUser.UserId)
                return HttpNotFound();

            // Check if event is lapsed
            if (proposal.Event.Status == "Lapsed" || proposal.Event.IsLapsed)
            {
                TempData["Error"] = "Cannot edit documents. This event has lapsed.";
                return RedirectToAction("Details", new { id = proposal.EventId });
            }

            if (proposal.Status != "Pending")
            {
                TempData["Error"] = "Only pending documents can be edited.";
                return RedirectToAction("Details", new { id = proposal.EventId });
            }

            if (uploadedFile != null && uploadedFile.ContentLength > 0)
            {
                string fileExtension = Path.GetExtension(uploadedFile.FileName).ToLower();

                if (fileExtension != ".pdf")
                {
                    ModelState.AddModelError("uploadedFile", "Only PDF files are allowed.");
                    return View(proposal);
                }

                string uploadFolder = Server.MapPath("~/Uploads");
                if (!Directory.Exists(uploadFolder))
                {
                    Directory.CreateDirectory(uploadFolder);
                }

                string oldFilePath = Server.MapPath(proposal.FilePath);
                if (System.IO.File.Exists(oldFilePath))
                {
                    System.IO.File.Delete(oldFilePath);
                }

                string originalFileName = Path.GetFileName(uploadedFile.FileName);
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
                string fileExt = Path.GetExtension(originalFileName);

                string uniqueFileName = $"{fileNameWithoutExt}_edit_{proposal.EventProposalId}_{DateTime.Now:yyyyMMddHHmmss}{fileExt}";
                string path = Path.Combine(uploadFolder, uniqueFileName);
                uploadedFile.SaveAs(path);
                proposal.FilePath = "/Uploads/" + uniqueFileName;
            }
            else
            {
                ModelState.AddModelError("uploadedFile", "Please upload a file.");
                return View(proposal);
            }

            proposal.SubmittedAt = DateTime.Now;
            db.SaveChanges();

            TempData["Success"] = "Document updated successfully.";
            return RedirectToAction("Details", new { id = proposal.EventId });
        }

        public ActionResult Resubmit(int id)
        {
            var proposal = db.EventProposals.Include(p => p.Feedbacks).Include(p => p.Event).FirstOrDefault(p => p.EventProposalId == id);
            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);

            if (proposal == null || proposal.ClientId != currentUser.UserId)
                return HttpNotFound();

            // Check if event is lapsed
            if (proposal.Event.Status == "Lapsed" || proposal.Event.IsLapsed)
            {
                TempData["Error"] = "Cannot resubmit documents. This event has lapsed.";
                return RedirectToAction("Details", new { id = proposal.EventId });
            }

            if (proposal.Status != "Rejected")
                return RedirectToAction("Index");

            return View(proposal);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Resubmit(int id, HttpPostedFileBase uploadedFile)
        {
            var proposal = db.EventProposals.Include(p => p.Event).FirstOrDefault(p => p.EventProposalId == id);
            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);

            if (proposal == null || proposal.ClientId != currentUser.UserId)
                return HttpNotFound();

            // Check if event is lapsed
            if (proposal.Event.Status == "Lapsed" || proposal.Event.IsLapsed)
            {
                TempData["Error"] = "Cannot resubmit documents. This event has lapsed.";
                return RedirectToAction("Details", new { id = proposal.EventId });
            }

            if (uploadedFile != null && uploadedFile.ContentLength > 0)
            {
                string fileExtension = Path.GetExtension(uploadedFile.FileName).ToLower();

                if (fileExtension != ".pdf")
                {
                    ModelState.AddModelError("uploadedFile", "Only PDF files are allowed.");
                    return View(proposal);
                }

                string uploadFolder = Server.MapPath("~/Uploads");
                if (!Directory.Exists(uploadFolder))
                {
                    Directory.CreateDirectory(uploadFolder);
                }

                string originalFileName = Path.GetFileName(uploadedFile.FileName);
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
                string fileExt = Path.GetExtension(originalFileName);

                string uniqueFileName = $"{fileNameWithoutExt}_resubmit_{proposal.EventProposalId}_{DateTime.Now:yyyyMMddHHmmss}{fileExt}";
                string path = Path.Combine(uploadFolder, uniqueFileName);
                uploadedFile.SaveAs(path);
                proposal.FilePath = "/Uploads/" + uniqueFileName;
            }
            else
            {
                ModelState.AddModelError("uploadedFile", "Please upload a file.");
                return View(proposal);
            }

            proposal.Status = "Resubmitted";
            proposal.SubmittedAt = DateTime.Now;

            var approvals = db.ProposalApprovals.Where(a => a.EventProposalId == proposal.EventProposalId).ToList();
            foreach (var approval in approvals)
            {
                approval.Status = "Pending";
                approval.ActionDate = null;

                var officeUsers = db.Users.Where(u => u.Role == approval.Office).ToList();

                foreach (var user in officeUsers)
                {
                    var notification = new Notification
                    {
                        Username = user.Username,
                        Message = $"A proposal titled '{proposal.Title}' has been resubmitted.",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    };
                    db.Notifications.Add(notification);
                }
            }

            db.SaveChanges();

            return RedirectToAction("Index");
        }

        [HttpPost]
        public ActionResult FollowUpAjax(int id)
        {
            try
            {
                var proposal = db.EventProposals.Include(p => p.Event).FirstOrDefault(p => p.EventProposalId == id);
                var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);

                if (proposal == null || proposal.ClientId != currentUser.UserId)
                    return Json(new { success = false, message = "Proposal not found." });

                // Check if event is lapsed
                if (proposal.Event.Status == "Lapsed" || proposal.Event.IsLapsed)
                    return Json(new { success = false, message = "Cannot follow up. This event has lapsed." });

                var pendingFollowUp = db.Notifications
                    .Any(n => n.Message.Contains("Follow up:") &&
                              n.Message.Contains(proposal.Title) &&
                              n.IsRead == false);

                if (pendingFollowUp)
                    return Json(new { success = false, message = "There is already a pending follow-up. Please wait for the office to read it first." });

                var originalNotificationRead = db.Notifications
                    .Any(n => n.Message.Contains(proposal.Title) &&
                              !n.Message.Contains("Follow up:") &&
                              n.IsRead == true);

                var previousFollowUpRead = db.Notifications
                    .Any(n => n.Message.Contains("Follow up:") &&
                              n.Message.Contains(proposal.Title) &&
                              n.IsRead == true);

                if (!originalNotificationRead && !previousFollowUpRead)
                    return Json(new { success = false, message = "The office has not read the document yet." });

                var officeUsers = db.Users.Where(u => u.Role == proposal.TargetOfficeRole).ToList();
                foreach (var user in officeUsers)
                {
                    db.Notifications.Add(new Notification
                    {
                        Username = user.Username,
                        Message = $"Follow up: Please review the proposal '{proposal.Title}' for event '{proposal.Event.Title}'.",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });
                }

                db.SaveChanges();

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "An error occurred: " + ex.Message });
            }
        }

        [HttpPost]
        public ActionResult ApproveEvent(int eventId, DateTime approvedDate)
        {
            var ev = db.Events
                .Include(e => e.Proposals)
                .FirstOrDefault(e => e.EventId == eventId);

            if (ev == null)
            {
                TempData["Error"] = "Event not found.";
                return RedirectToAction("PresidentDashboard", "Dashboard");
            }

            ev.Status = "ApprovedByPresident";
            ev.ApprovedDate = approvedDate;

            db.SaveChanges();

            var client = db.Users.FirstOrDefault(u => u.UserId == ev.ClientId);
            if (client != null)
            {
                db.Notifications.Add(new Notification
                {
                    Username = client.Username,
                    Message = $"Your event '{ev.Title}' has been approved by the President and scheduled for {approvedDate:MMMM dd, yyyy}.",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
                db.SaveChanges();
            }

            TempData["Success"] = "Event approved successfully.";
            return RedirectToAction("PresidentDashboard", "Dashboard");
        }

        public ActionResult PresidentEventDetails(int id)
        {
            var ev = db.Events.Include("Client").Include("Proposals").FirstOrDefault(e => e.EventId == id);
            if (ev == null) return HttpNotFound();
            return View("PresidentEventDetails", ev);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult PresidentApprove(int eventId)
        {
            var ev = db.Events.FirstOrDefault(e => e.EventId == eventId);
            if (ev == null) return HttpNotFound();
            ev.Status = "ApprovedByPresident";
            db.SaveChanges();
            TempData["Success"] = "Event approved by President.";
            return RedirectToAction("PresidentDashboard", "Dashboard");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult PresidentReject(int eventId)
        {
            var ev = db.Events.FirstOrDefault(e => e.EventId == eventId);
            if (ev == null) return HttpNotFound();
            ev.Status = "RejectedByPresident";
            db.SaveChanges();
            TempData["Success"] = "Event rejected by President.";
            return RedirectToAction("PresidentDashboard", "Dashboard");
        }

        public ActionResult Feedback(int id)
        {
            var proposal = db.EventProposals.Find(id);
            if (proposal == null || proposal.Client.Username != User.Identity.Name)
            {
                return HttpNotFound();
            }

            return View(proposal);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Feedback(int id, string feedback)
        {
            var proposal = db.EventProposals.Include(p => p.Feedbacks).FirstOrDefault(p => p.EventProposalId == id);
            if (proposal != null && proposal.Client.Username == User.Identity.Name)
            {
                var feedbackEntry = new ProposalFeedback
                {
                    EventProposalId = proposal.EventProposalId,
                };

                proposal.Status = "Resubmitted";
                proposal.Feedbacks.Add(feedbackEntry);

                db.SaveChanges();

                return RedirectToAction("Index");
            }

            return View(proposal);
        }

        [HttpGet]
        public ActionResult FollowUp(int id)
        {
            var proposal = db.EventProposals.Include(p => p.Event).FirstOrDefault(p => p.EventProposalId == id);
            var currentUser = db.Users.FirstOrDefault(u => u.Username == User.Identity.Name);
            if (proposal == null || proposal.ClientId != currentUser.UserId)
                return HttpNotFound();

            // Check if event is lapsed
            if (proposal.Event.Status == "Lapsed" || proposal.Event.IsLapsed)
            {
                TempData["Error"] = "Cannot follow up. This event has lapsed.";
                return RedirectToAction("Details", new { id = proposal.EventId });
            }

            if (proposal.HasFollowedUp)
            {
                TempData["Error"] = "You have already used Follow Up for this proposal.";
                return RedirectToAction("Details", new { id = proposal.EventId });
            }
            proposal.HasFollowedUp = true;
            var officeUsers = db.Users.Where(u => u.Role == proposal.TargetOfficeRole).ToList();
            foreach (var user in officeUsers)
            {
                db.Notifications.Add(new Notification
                {
                    Username = user.Username,
                    Message = $"Follow up: Please review the proposal '{proposal.Title}'.",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }
            db.SaveChanges();
            TempData["Success"] = "Follow up sent to the office.";
            return RedirectToAction("Details", new { id = proposal.EventId });
        }
    }
}