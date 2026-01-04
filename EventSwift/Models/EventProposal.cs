using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.ComponentModel.DataAnnotations;

namespace EventSwift.Models
{
    public class EventProposal
    {
        public int EventProposalId { get; set; }
        public string Title { get; set; }
        public string FilePath { get; set; }
        public string Status { get; set; } // Pending, Approved, Rejected, Resubmitted, Lapsed
        public DateTime SubmittedAt { get; set; }

        // Link to Event
        public int EventId { get; set; }
        public virtual Event Event { get; set; }

        public int ClientId { get; set; }
        public virtual User Client { get; set; }

        public string TargetOfficeRole { get; set; }

        public bool HasFollowedUp { get; set; }

        public virtual ICollection<ProposalApproval> Approvals { get; set; }
        public virtual ICollection<ProposalFeedback> Feedbacks { get; set; }
    }
}