using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Web.Mvc;
using EquipmentRental.Models;

namespace EquipmentRental.Controllers
{
    public class RentalController : Controller
    {
        private readonly MyDbContext db = new MyDbContext();

        /* USER ‑‑ MY RENTALS  (card view) */
        [Authorize]
        public ActionResult Index(string q = null)
        {
            int uid = (int)Session["UserID"];

            IQueryable<Rental> query = db.Rentals
                                         .Include(r => r.Equipment)
                                         .Where(r => r.RenterID == uid);

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(r => r.Equipment.Name.Contains(q));

            List<Rental> list = query
                                .OrderByDescending(r => r.RentalStartDate)
                                .ToList();

            if (Request.IsAjaxRequest())
                return PartialView("_UserRentalCards", list);

            return View(list);
        }

        /* ADMIN ‑‑ ALL RENTALS  (table view) */
        [Authorize(Roles = "Admin")]
        public ActionResult AdminIndex(string q = null)
        {
            IQueryable<Rental> query = db.Rentals
                                         .Include(r => r.Equipment.Owner)
                                         .Include(r => r.Renter);

            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(r =>
                    r.Equipment.Name.Contains(q) ||
                    r.Equipment.Owner.Name.Contains(q) ||
                    r.Renter.Name.Contains(q));
            }

            List<Rental> list = query
                                .OrderByDescending(r => r.RentalStartDate)
                                .ToList();

            if (Request.IsAjaxRequest())
                return PartialView("_RentalTable", list);

            return View("AdminIndex", list);
        }

        /* AUTOCOMPLETE  (equipment names) */
        [HttpGet, Authorize]
        public JsonResult AutoComplete(string term)
        {
            List<string> names = db.Rentals
                                   .Where(r => r.Equipment.Name.Contains(term))
                                   .Select(r => r.Equipment.Name)
                                   .Distinct()
                                   .Take(10)
                                   .ToList();

            return Json(names, JsonRequestBehavior.AllowGet);
        }

        /* DETAILS */
        [Authorize]
        public ActionResult Details(int id)
        {
            Rental rental = db.Rentals
                              .Include(r => r.Equipment.Owner)
                              .Include(r => r.Renter)
                              .FirstOrDefault(r => r.ID == id);

            if (rental == null) return HttpNotFound();

            bool allowed = User.IsInRole("Admin") ||
                           (Session["UserID"] as int?) == rental.RenterID;
            if (!allowed) return new HttpStatusCodeResult(403);

            ViewBag.BookedRanges = db.Rentals
                                     .Where(r => r.EquipmentID == rental.EquipmentID &&
                                                 r.Status == "Approved")
                                     .OrderBy(r => r.RentalStartDate)
                                     .ToList();

            return View(rental);
        }

        /* CREATE  (GET) */
        [Authorize]
        public ActionResult Create(int? equipmentId = null)
        {
            if (equipmentId == null)
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);

            Equipment equip = db.Equipments.Find(equipmentId);
            if (equip == null || !equip.IsApproved) return HttpNotFound();

            ViewBag.Equipment = equip;

            ViewBag.BookedRanges = db.Rentals
                                     .Where(r => r.EquipmentID == equip.ID &&
                                                 r.Status == "Approved")
                                     .OrderBy(r => r.RentalStartDate)
                                     .ToList();

            return View(new Rental { EquipmentID = equip.ID });
        }

        /* CREATE  (POST) */
        [HttpPost, ValidateAntiForgeryToken, Authorize]
        public ActionResult Create(
            [Bind(Include = "EquipmentID,RentalStartDate,RentalEndDate")]
            Rental rental)
        {
            Equipment equip = db.Equipments
                                .Include(e => e.Owner)
                                .FirstOrDefault(e => e.ID == rental.EquipmentID);

            if (equip == null || !equip.IsApproved) return HttpNotFound();

            int uid = (int)Session["UserID"];

            if (rental.RentalStartDate >= rental.RentalEndDate)
                ModelState.AddModelError("", "End date must be after start date.");

            /* overlapping approved rentals (chronological) */
            List<Rental> overlaps = db.Rentals
                .Where(r => r.EquipmentID == equip.ID &&
                            r.Status == "Approved" &&
                            r.RentalEndDate >= rental.RentalStartDate &&
                            r.RentalStartDate <= rental.RentalEndDate)
                .OrderBy(r => r.RentalStartDate)
                .ToList();

            if (overlaps.Any())
            {
                IEnumerable<string> ranges = overlaps.Select(r =>
                    string.Format("{0:MM/dd/yyyy} – {1:MM/dd/yyyy}",
                                  r.RentalStartDate,
                                  r.RentalEndDate));

                string msg = "Cannot book. Equipment already rented on: "
                           + string.Join("; ", ranges);

                ModelState.AddModelError("_Conflict", msg);
            }

            if (!ModelState.IsValid)
            {
                ViewBag.Equipment = equip;
                ViewBag.BookedRanges = db.Rentals
                                         .Where(r => r.EquipmentID == equip.ID &&
                                                     r.Status == "Approved")
                                         .OrderBy(r => r.RentalStartDate)
                                         .ToList();
                return View(rental);
            }

            int days = (rental.RentalEndDate - rental.RentalStartDate).Days + 1;
            rental.TotalCost = days * equip.RentalPrice;
            rental.RenterID = uid;
            rental.Status = "Pending";
            rental.ApproverID = null;

            db.Rentals.Add(rental);
            db.SaveChanges();

            return RedirectToAction("Index");
        }

        /* EDIT  (GET)*/
        [Authorize]
        public ActionResult Edit(int id)
        {
            Rental rental = db.Rentals
                              .Include(r => r.Equipment.Owner)
                              .Include(r => r.Renter)
                              .FirstOrDefault(r => r.ID == id);

            if (rental == null) return HttpNotFound();

            bool owner = (Session["UserID"] as int?) == rental.RenterID;
            if (rental.Status != "Pending" || (!owner && !User.IsInRole("Admin")))
                return new HttpStatusCodeResult(403);

            return View(rental);
        }

        /* EDIT  (POST) */
        [HttpPost, ValidateAntiForgeryToken, Authorize]
        public ActionResult Edit(
            [Bind(Include = "ID,RentalStartDate,RentalEndDate")]
            Rental edited)
        {
            Rental rental = db.Rentals
                              .Include(r => r.Equipment)
                              .FirstOrDefault(r => r.ID == edited.ID);

            if (rental == null) return HttpNotFound();

            bool owner = (Session["UserID"] as int?) == rental.RenterID;
            if (rental.Status != "Pending" || (!owner && !User.IsInRole("Admin")))
                return new HttpStatusCodeResult(403);

            if (edited.RentalStartDate >= edited.RentalEndDate)
                ModelState.AddModelError("", "End date must be after start date.");

            bool overlap = db.Rentals.Any(r =>
                r.ID != rental.ID &&
                r.EquipmentID == rental.EquipmentID &&
                r.Status == "Approved" &&
                r.RentalEndDate >= edited.RentalStartDate &&
                r.RentalStartDate <= edited.RentalEndDate);

            if (overlap)
                ModelState.AddModelError("",
                    "Requested dates conflict with an approved rental.");

            if (!ModelState.IsValid) return View(rental);

            rental.RentalStartDate = edited.RentalStartDate;
            rental.RentalEndDate = edited.RentalEndDate;

            int days = (edited.RentalEndDate - edited.RentalStartDate).Days + 1;
            rental.TotalCost = days * rental.Equipment.RentalPrice;

            db.SaveChanges();
            return RedirectToAction("Index");
        }

        /*DELETE  (GET) */
        [Authorize]
        public ActionResult Delete(int id)
        {
            Rental rental = db.Rentals
                              .Include(r => r.Equipment.Owner)
                              .Include(r => r.Renter)
                              .FirstOrDefault(r => r.ID == id);

            if (rental == null) return HttpNotFound();

            bool owner = (Session["UserID"] as int?) == rental.RenterID;
            if (!owner && !User.IsInRole("Admin"))
                return new HttpStatusCodeResult(403);

            return View(rental);
        }

        /* DELETE  (POST) */
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken, Authorize]
        public ActionResult DeleteConfirmed(int id)
        {
            Rental rental = db.Rentals.Find(id);
            if (rental == null) return HttpNotFound();

            bool owner = (Session["UserID"] as int?) == rental.RenterID;
            if (!owner && !User.IsInRole("Admin"))
                return new HttpStatusCodeResult(403);

            db.Rentals.Remove(rental);
            db.SaveChanges();

            return RedirectToAction("Index");
        }

        /* APPROVE  (Admin) */
        [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
        public ActionResult Approve(int id)
        {
            Rental rental = db.Rentals
                              .Include(r => r.Equipment)
                              .FirstOrDefault(r => r.ID == id);

            if (rental == null) return HttpNotFound();
            if (rental.Status != "Pending")
                return new HttpStatusCodeResult(400, "Rental is not pending.");

            bool overlap = db.Rentals.Any(r =>
                r.ID != rental.ID &&
                r.EquipmentID == rental.EquipmentID &&
                r.Status == "Approved" &&
                r.RentalEndDate >= rental.RentalStartDate &&
                r.RentalStartDate <= rental.RentalEndDate);

            if (overlap)
            {
                TempData["Error"] = "Cannot approve: dates overlap an existing approved rental.";
                return RedirectToAction("ManageRequests", "Admin");
            }

            rental.Status = "Approved";
            rental.ApproverID = (int)Session["UserID"];

            db.SaveChanges();
            return RedirectToAction("ManageRequests", "Admin");
        }

        /* DENY  (Admin) – delete the pending request*/
        [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
        public ActionResult Deny(int id)
        {
            Rental rental = db.Rentals.Find(id);
            if (rental == null) return HttpNotFound();
            if (rental.Status != "Pending")
                return new HttpStatusCodeResult(400, "Rental is not pending.");

            db.Rentals.Remove(rental);
            db.SaveChanges();

            return RedirectToAction("ManageRequests", "Admin");
        }

        /*  DISPOSE */
        protected override void Dispose(bool disposing)
        {
            if (disposing) db.Dispose();
            base.Dispose(disposing);
        }
    }
}
