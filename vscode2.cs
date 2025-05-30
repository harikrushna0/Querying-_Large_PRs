using CAAPS.Enums;
using CAAPS.Swift.MT564;
using CAAPS.SwiftDbHandler;
using Caaps_Models.Display;
using Caaps_Models.Notifications;
using CaapsWebServer.AccessLayer.Implementation;
using CaapsWebServer.AccessLayer.Interfaces;
using CaapsWebServer.Config;
using CaapsWebServer.DataLayer.DataModel;
using CaapsWebServer.DataLayer.Interfaces;
using CaapsWebServer.Enums;
using CaapsWebServer.Models.Generic;
using CaapsWebServer.Models.Response;
using CaapsWebServer.Solace;
using LogUtility.Logger;
using Microsoft.Extensions.Options;
using MoreLinq.Extensions;
using System;
using System.Collections.Generic;
using System.Dynamic;
using Caaps_Models.Solace;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using System.Text.Json;
using System.Globalization;

using Microsoft.EntityFrameworkCore;
using CaapsWebServer.Utilities;
using CAAPS;
using Microsoft.Extensions.Configuration;
using CAAPS.Common.Security;
using System.Text.RegularExpressions;
//using CAAPS.SwiftDb.Common.Security;
using System.Collections.ObjectModel;
using CaapsWebServer.Models.Request;
using Newtonsoft.Json.Linq;
using LinqToDB.Common;


namespace CaapsWebServer.DataLayer.Implementation
{
    public class EventDBHandler : IEventDBHandler
    {
        private readonly SwiftDbContextWeb swiftContext = null;
        private readonly CAAPSContext context = null;
        private readonly IEnumHelper enumHelper = null;
        private readonly LinqToDbHelper linqToDbHelper = null;

        private readonly CaapsLinqToDB.DBModels.LinqToDBSwiftContext linqToDBContext = null;

        private readonly DisplayServiceHelper displayDataHelper = null;

        private readonly DisplayToMt564 displayToMt = null;
        private readonly IAdvanceSearchService advanceSearchService = null;
        private readonly SolaceMessageListener solaceMessageListener = null;
        private readonly SolaceSettings solaceSettings;
        private readonly IAuditTrailOperationalDBHandler auditTrailDBHandler = null;
        private readonly AppNotificationSender appNotificationSender = null;
        private readonly IConfiguration configuration;

        public EventDBHandler(IConfiguration _configuration, SwiftDbContextWeb swiftContext, CAAPSContext context, IEnumHelper enumHelper, DisplayServiceHelper displayDataHelper, IAdvanceSearchService advanceSearchService, DisplayToMt564 displayToMt, SolaceMessageListener solaceMessageListener, IOptionsMonitor<SolaceSettings> solaceSettings, IAuditTrailOperationalDBHandler auditTrailDBHandler, LinqToDbHelper linqToDbHelper, CaapsLinqToDB.DBModels.LinqToDBSwiftContext linqToDBContext, AppNotificationSender appNotificationSender)
        {
            this.swiftContext = swiftContext;
            this.context = context;
            this.enumHelper = enumHelper;
            this.displayDataHelper = displayDataHelper;
            this.advanceSearchService = advanceSearchService;
            this.displayToMt = displayToMt;
            this.solaceMessageListener = solaceMessageListener;
            this.solaceSettings = solaceSettings.CurrentValue;
            this.auditTrailDBHandler = auditTrailDBHandler;
            this.linqToDbHelper = linqToDbHelper;
            this.linqToDBContext = linqToDBContext;
            this.appNotificationSender = appNotificationSender;
            this.configuration = _configuration;
        }

        public ResponseParent EventApprovalRejection(int goldenRecordId, int Status, string Role, ApproveReject detail)
        {
            ResponseParent res = new ResponseParent();
            try
            {
                Logger.Log("DisplayObject = " + JsonConvert.SerializeObject(detail.displayData), LogType.Debug);
                Logger.Log("ApproveReject detail: IsSuppressNotificationEnabled = " + detail.IsSuppressNotificationEnabled + ", Action: " + (detail.IsSuppressNotificationEnabled ? "Suppressing notifications" : "Notifications enabled"), LogType.Debug);
                if (Role != UserRoleConsts.SUPERVISOR && Role != UserRoleConsts.SYSTEM_ADMIN)
                {
                    res.IsSuccess = false;
                    res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NoPermission);
                    res.MessageId = (int)ResponseCodeMessage.NoPermission;
                }
                else
                {
                    GoldenRecordInternalChatDetail newComment = new GoldenRecordInternalChatDetail();
                    newComment.ChatText = detail.comment;
                    newComment.GoldenRecordId = goldenRecordId;
                    newComment.IsActive = 1;
                    newComment.OriginatedFromAction = detail.OriginatedFromAction;
                    newComment.EntryBy = detail.EntryBy;
                    newComment.EntryDtTime = DateTime.Now;
                    newComment.EntryDtTimeUtc = DateTime.UtcNow;
                    this.context.GoldenRecordInternalChatDetails.Add(newComment);
                    this.context.SaveChanges();

                    List<CaEventConflict> allConflicts = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == goldenRecordId).ToList();
                    GoldenRecordMst g = this.context.GoldenRecordMsts.Where(x => x.GoldenRecordId == goldenRecordId).FirstOrDefault();
                    CaapsLinqToDB.DBModels.MT564Message message = this.linqToDbHelper.GetEventDetailsByGolderRecordId((long)goldenRecordId);

                    GoldenRecordMst grm = this.context.GoldenRecordMsts.Where(x => x.GoldenRecordId == goldenRecordId).FirstOrDefault();
                    string[] GPDFields = new string[] { };

                    GPDFields = (from cg in this.context.CaEventConfigs
                                 join cem in this.context.CaEventTypeMsts on cg.CaEventTypeId equals cem.CaEventTypeId
                                 join ce in this.context.CaEventMsts on cg.CaEventMstId equals ce.CaEventMstId
                                 where ce.CaEventCode == grm.EventName && cem.CaEventType == grm.EventMvc
                                 select cg.GpdDateType).FirstOrDefault().Split(',');

                    if (GPDFields.Length == 1)
                    {
                        grm.GpdDateType = GPDFields[0];
                        this.context.GoldenRecordMsts.Update(grm);
                        this.context.SaveChanges();
                    }
                    else if (GPDFields.Length > 0)
                    {
                        string matchDateData = System.IO.File.ReadAllText("./MatchDates.json", Encoding.UTF8);
                        var dateField = System.Text.Json.JsonSerializer.Deserialize<List<MatchDateModel>>(matchDateData);
                        IDictionary<string, DateTime?> objGPDFieldsData = new Dictionary<string, DateTime?>();
                        for (int i = 0; i < GPDFields.Length; i++)
                        {
                            objGPDFieldsData.Add(
                                GPDFields[i],
                                detail.displayData.GetType().GetProperty(dateField.Where(x => x.Qualifier == GPDFields[i]).FirstOrDefault().MatchDate).GetValue(detail.displayData) == null ? null :
                                Convert.ToDateTime(detail.displayData.GetType().GetProperty(dateField.Where(x => x.Qualifier == GPDFields[i]).FirstOrDefault().MatchDate).GetValue(detail.displayData).ToString())
                                );
                        }

                        if (!objGPDFieldsData.Any(x => x.Value != null))
                        {
                            grm.GpdDateType = "NONE";
                        }
                        else if (objGPDFieldsData.Where(x => x.Value != null).Count() == 1)
                        {
                            grm.GpdDateType = objGPDFieldsData.Where(x => x.Value != null).FirstOrDefault().Key;
                        }
                        else if (objGPDFieldsData.Where(x => x.Value != null).Count() > 1)
                        {
                            grm.GpdDateType = objGPDFieldsData.Where(x => x.Value != null).OrderBy(x => x.Value).FirstOrDefault().Key;
                        }
                        this.context.GoldenRecordMsts.Update(grm);
                        this.context.SaveChanges();
                    }


                    if (Status == 1) //Approved
                    {
                        List<CaEventConflict> conflicts = allConflicts.Where(x => x.ReviewStatus == (int)EventReviewStatus.InReview).ToList();

                        if (g.GoldenRecordStatus == (string)EventWorkFlowStatus.PendingCancel)
                        {
                            if (g != null)
                            {
                                message.MessageData.GeneralInformation.FunctionOfMessage.Function = "CANC";
                                message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.COMP;
                                AdditionalInfoFlags flagDetails = this.linqToDbHelper.getAdditionFlags(message);
                                this.linqToDBContext.Update(message);
                                this.linqToDBContext.SaveChanges();
                                Logger.Log($"EventApprovalRejection EventWorkFlowStatus.Cancelled", LogType.Info);
                                g.GoldenRecordStatus = EventWorkFlowStatus.Cancelled;
                                g.ReviewByDate = null;
                                g.IsAdditionalTextUpdated = flagDetails.IsAdditionTextUpdated;
                                g.IsInformationConditionsUpdated = flagDetails.IsInformationConditionsUpdated;
                                g.IsInformationToComplyUpdated = flagDetails.IsInformationToComplyUpdated;
                                g.IsNarrativeVersionUpdated = flagDetails.IsNarrativeVersionUpdated;
                                g.IsTaxationConditionsUpdated = flagDetails.IsTaxationConditionUpdated;
                                g.EarlyResponseDeadlineDate = flagDetails.EarlyResponseDeadline;
                                g.ProtectDate = flagDetails.ProtectDate;
                                g.ResponseDeadlineDate = flagDetails.ResponseDeadline;
                                g.MarketDeadlineDate = flagDetails.MarketDeadline;
                                g.IsoChangeType = flagDetails.ISOChangeType;
                                g.IsoEffectiveDate = flagDetails.ISOEffectiveDate;
                                g.IsoExDividendOrDistributionDate = flagDetails.ISOExDividendDate;
                                g.IsoExpiryDate = flagDetails.ISOExpiryDate;
                                g.IsoOfferor = flagDetails.ISOOfferor;
                                g.IsoRecordDate = flagDetails.ISORecordDate;
                                g.IsTouched = true;
                                g.IsAutoStp = false;
                                g.EntryDtTime = DateTime.Now;
                                g.EntryDtTimeUtc = DateTime.UtcNow;
                                g.EntryBy = detail.EntryBy;
                                this.context.SaveChanges();

                                this.SetResponseDeadlineDates(goldenRecordId, message, detail.EntryBy);

                                res.IsSuccess = true;
                                res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                                res.MessageId = (int)ResponseCodeMessage.Successful;
                                /*
                                    As per CR#110 we dont need to send position notifications to client when Suppress Notification Enable check is false.
                                */
                                if (!detail.IsSuppressNotificationEnabled)
                                {
                                    ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                                    Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                                }
                            }
                            else
                            {
                                res.IsSuccess = false;
                                res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NotFound);
                                res.MessageId = (int)ResponseCodeMessage.NotFound;
                            }
                        }
                        else if (g.GoldenRecordStatus == (string)EventWorkFlowStatus.PendingWithdraw)
                        {
                            if (g != null)
                            {
                                message.MessageData.GeneralInformation.FunctionOfMessage.Function = "WITH";
                                message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.COMP;
                                AdditionalInfoFlags flagDetails = this.linqToDbHelper.getAdditionFlags(message);
                                this.linqToDBContext.Update(message);
                                this.linqToDBContext.SaveChanges();
                                Logger.Log($"EventApprovalRejection EventWorkFlowStatus.Withdrawn", LogType.Info);
                                g.GoldenRecordStatus = EventWorkFlowStatus.Withdrawn;
                                g.IsAdditionalTextUpdated = flagDetails.IsAdditionTextUpdated;
                                g.IsInformationConditionsUpdated = flagDetails.IsInformationConditionsUpdated;
                                g.IsInformationToComplyUpdated = flagDetails.IsInformationToComplyUpdated;
                                g.IsNarrativeVersionUpdated = flagDetails.IsNarrativeVersionUpdated;
                                g.IsTaxationConditionsUpdated = flagDetails.IsTaxationConditionUpdated;
                                g.IsTouched = true;
                                g.EarlyResponseDeadlineDate = flagDetails.EarlyResponseDeadline;
                                g.ProtectDate = flagDetails.ProtectDate;
                                g.ResponseDeadlineDate = flagDetails.ResponseDeadline;
                                g.MarketDeadlineDate = flagDetails.MarketDeadline;
                                g.IsoChangeType = flagDetails.ISOChangeType;
                                g.IsoEffectiveDate = flagDetails.ISOEffectiveDate;
                                g.IsoExDividendOrDistributionDate = flagDetails.ISOExDividendDate;
                                g.IsoExpiryDate = flagDetails.ISOExpiryDate;
                                g.IsoOfferor = flagDetails.ISOOfferor;
                                g.IsoRecordDate = flagDetails.ISORecordDate;
                                g.ReviewByDate = null;
                                g.IsAutoStp = false;
                                g.EntryDtTime = DateTime.Now;
                                g.EntryDtTimeUtc = DateTime.UtcNow;
                                g.EntryBy = detail.EntryBy;
                                this.context.SaveChanges();
                                this.SetResponseDeadlineDates(goldenRecordId, message, detail.EntryBy);
                                res.IsSuccess = true;
                                res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                                res.MessageId = (int)ResponseCodeMessage.Successful;
                                /*
                                    As per CR#110 we dont need to send position notifications to client when Suppress Notification Enable check is false.
                                */
                                if (!detail.IsSuppressNotificationEnabled)
                                {
                                    ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                                    Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                                }
                            }
                            else
                            {
                                res.IsSuccess = false;
                                res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NotFound);
                                res.MessageId = (int)ResponseCodeMessage.NotFound;
                            }
                        }
                        else if (g.GoldenRecordStatus == (string)EventWorkFlowStatus.PendingDelete)
                        {
                            DeleteEventModel model = new DeleteEventModel();
                            model.MessageFrom = Notification.Component.WEB_SERVER;
                            model.MessageAction = "DELETE_EVENT";
                            model.GoldenRecordId = goldenRecordId;
                            model.IsSuppressNotificationEnabled = detail.IsSuppressNotificationEnabled;
                            if (solaceSettings.SendToTopic)
                            {
                                this.solaceMessageListener.SendToTopic(solaceSettings.MatchingTopicName, JsonConvert.SerializeObject(model));
                            }
                            else
                            {
                                this.solaceMessageListener.SendToQueue(solaceSettings.MatchingQueueName, JsonConvert.SerializeObject(model));
                            }
                            res.IsSuccess = true;
                            res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                            res.MessageId = (int)ResponseCodeMessage.Successful;
                        }
                        else
                        {

                            conflicts.ForEach(conflict =>
                            {
                                conflict.FieldStatus = (byte)EventFieldStatus.Complete;
                                conflict.ReviewStatus = (byte)EventReviewStatus.UserResolved;
                                conflict.EntryBy = detail.EntryBy;
                                conflict.EntryDtTime = DateTime.Now;
                                conflict.EntryDtTimeUtc = DateTime.UtcNow;

                                if (conflict.FieldName.Contains("securityid"))
                                {
                                    GoldenRecordSecurityDetail existingObject = this.context.GoldenRecordSecurityDetails.Where(x => x.GoldenRecordId == conflict.GoldenRecordId
                                    && x.OptionNumber == conflict.OptionNumber
                                    && x.MovementId == conflict.MovementId).FirstOrDefault();
                                    var secIdType = detail.displayData.Options.Where(x =>
                                                    x.CaOptOptionNumber == conflict.OptionNumber)
                                                    .FirstOrDefault().securityMovement.Where(y =>
                                                        y.SMSecurityNumber == conflict.MovementId)
                                                    .FirstOrDefault().SMNewSecurityIDType;
                                    var secId = detail.displayData.Options.Where(x =>
                                                x.CaOptOptionNumber == conflict.OptionNumber)
                                                .FirstOrDefault().securityMovement.Where(y =>
                                                    y.SMSecurityNumber == conflict.MovementId)
                                                .FirstOrDefault().SMNewSecurityID;

                                    if (existingObject == null)
                                    {
                                        existingObject = new GoldenRecordSecurityDetail();
                                        existingObject.GoldenRecordId = conflict.GoldenRecordId;
                                        existingObject.OptionNumber = conflict.OptionNumber;
                                        existingObject.MovementId = conflict.MovementId;
                                        existingObject.CUSIP = secIdType == "CUSP" ? secId : "";
                                        existingObject.ISIN = secIdType == "ISIN" ? secId : "";
                                        existingObject.Sedol = secIdType == "Sedol" ? secId : "";
                                        existingObject.BloombergId = "";
                                        existingObject.ShortName = "";
                                        existingObject.LongName = "";
                                        existingObject.IsActive = 1;
                                        existingObject.EntryBy = detail.EntryBy;
                                        existingObject.EntryDtTime = DateTime.Now;
                                        existingObject.EntryDtTimeUtc = DateTime.UtcNow;
                                        this.context.GoldenRecordSecurityDetails.Add(existingObject);
                                    }
                                    else
                                    {
                                        if ((secIdType == "CUSP" && secId != existingObject.CUSIP)
                                        || (secIdType == "ISIN" && secId != existingObject.ISIN)
                                        || (secIdType == "Sedol" && secId != existingObject.Sedol)
                                        )
                                        {
                                            existingObject.CUSIP = secIdType == "CUSP" ? secId : "";
                                            existingObject.ISIN = secIdType == "ISIN" ? secId : "";
                                            existingObject.Sedol = secIdType == "Sedol" ? secId : "";
                                            existingObject.BloombergId = "";
                                            existingObject.ShortName = "";
                                            existingObject.LongName = "";
                                            this.context.GoldenRecordSecurityDetails.Update(existingObject);
                                        }
                                    }
                                    this.context.SaveChanges();
                                }

                                this.context.SaveChanges();
                            });

                            //getting null pointer exception from TransformDisplayToMt564 issue fix
                            CaapsLinqToDB.DBModels.MT564Message cloneMessage = JsonConvert.DeserializeObject<CaapsLinqToDB.DBModels.MT564Message>(JsonConvert.SerializeObject(message, Formatting.None, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore }));
                            CaapsLinqToDB.DBModels.MT564Message UpdatedMessage = this.linqToDbHelper.TransformDisplayToMt564(message, detail.displayData);
                            UpdatedMessage.MessageData.GeneralInformation.FunctionOfMessage.Function = "REPL";
                            UpdatedMessage.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.COMP;

                            Boolean GPDUpdateOrNot = this.setGPDDate(cloneMessage, UpdatedMessage, detail.displayData.EventName, detail.displayData.EventMvc);
                            AdditionalInfoFlags flagDetails = this.linqToDbHelper.getAdditionFlags(UpdatedMessage);
                            Logger.Log("Updated MT564 Message = " + JsonConvert.SerializeObject(UpdatedMessage, Formatting.None, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore }), LogType.Debug);

                            this.linqToDBContext.Update(UpdatedMessage);
                            this.linqToDBContext.SaveChanges();

                            if (g != null)
                            {
                                Logger.Log($"EventApprovalRejection EventWorkFlowStatus.Complete", LogType.Info);
                                g.GoldenRecordStatus = EventWorkFlowStatus.Complete;
                                g.IsAdditionalTextUpdated = flagDetails.IsAdditionTextUpdated;
                                g.IsInformationConditionsUpdated = flagDetails.IsInformationConditionsUpdated;
                                g.IsInformationToComplyUpdated = flagDetails.IsInformationToComplyUpdated;
                                g.IsNarrativeVersionUpdated = flagDetails.IsNarrativeVersionUpdated;
                                g.IsTaxationConditionsUpdated = flagDetails.IsTaxationConditionUpdated;
                                g.IsTouched = true;
                                g.EarlyResponseDeadlineDate = flagDetails.EarlyResponseDeadline;
                                g.ProtectDate = flagDetails.ProtectDate;
                                g.ResponseDeadlineDate = flagDetails.ResponseDeadline;
                                g.MarketDeadlineDate = flagDetails.MarketDeadline;
                                g.IsoChangeType = flagDetails.ISOChangeType;
                                g.IsoEffectiveDate = flagDetails.ISOEffectiveDate;
                                g.IsoExDividendOrDistributionDate = flagDetails.ISOExDividendDate;
                                g.IsoExpiryDate = flagDetails.ISOExpiryDate;
                                g.IsoOfferor = flagDetails.ISOOfferor;
                                g.IsoRecordDate = flagDetails.ISORecordDate;
                                g.ReviewByDate = null;
                                g.IsAutoStp = false;
                                g.EntryDtTime = DateTime.Now;
                                g.EntryDtTimeUtc = DateTime.UtcNow;
                                g.EntryBy = detail.EntryBy;
                                if (GPDUpdateOrNot == true)
                                {
                                    g.PositionFixDate = null;
                                    g.IsPositionCaptured = false;
                                    this.context.Database.ExecuteSqlRaw($"Exec Proc_Web_Submit_GoldenRecord_Position_Flag_Update_All_Related_Securities {goldenRecordId}");
                                }
                                this.context.SaveChanges();
                                this.SetResponseDeadlineDates(goldenRecordId, message, detail.EntryBy);
                                res.IsSuccess = true;
                                res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                                res.MessageId = (int)ResponseCodeMessage.Successful;
                                /*
                                    As per CR#110 we dont need to send position notifications to client when Suppress Notification Enable check is false.
                                */
                                if (!detail.IsSuppressNotificationEnabled)
                                {
                                    ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, UpdatedMessage, g);
                                    Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                                }
                            }
                            else
                            {
                                res.IsSuccess = false;
                                res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NotFound);
                                res.MessageId = (int)ResponseCodeMessage.NotFound;
                            }
                        }
                    }
                    else //Rejected
                    {
                        List<CaEventConflict> conflicts = allConflicts.Where(x => x.ReviewStatus == (int)EventReviewStatus.InReview).ToList();
                        conflicts.ForEach(conflict =>
                        {
                            conflict.ReviewStatus = (byte)EventReviewStatus.Open;
                            conflict.EntryDtTime = DateTime.Now;
                            conflict.EntryDtTimeUtc = DateTime.UtcNow;
                            this.context.SaveChanges();
                        });

                        if (g != null)
                        {
                            List<int> fieldStatus = allConflicts.Select(x => x.FieldStatus).ToList();
                            int finalStatus = fieldStatus.Max();
                            if (finalStatus == (int)EventFieldStatus.Incomplete)
                            {
                                Logger.Log($"EventApprovalRejection Rejected EventWorkFlowStatus.Incomplete", LogType.Info);
                                g.GoldenRecordStatus = EventWorkFlowStatus.Incomplete;
                                message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREU;
                            }
                            else if (finalStatus == (int)EventFieldStatus.Conflict)
                            {
                                Logger.Log($"EventApprovalRejection Rejected EventWorkFlowStatus.Conflict", LogType.Info);
                                g.GoldenRecordStatus = EventWorkFlowStatus.Conflict;
                                message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREU;
                            }
                            else if (finalStatus == (int)EventFieldStatus.Complete)
                            {
                                Logger.Log($"EventApprovalRejection Rejected EventWorkFlowStatus.Complete", LogType.Info);
                                g.GoldenRecordStatus = EventWorkFlowStatus.Complete;
                                message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREU;
                            }
                            else if (finalStatus == (int)EventFieldStatus.Updated)
                            {
                                Logger.Log($"EventApprovalRejection Rejected EventWorkFlowStatus.Updated", LogType.Info);
                                g.GoldenRecordStatus = EventWorkFlowStatus.Updated;
                                message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREU;
                            }
                            else
                            {
                                GoldenRecordMstAudit ga = this.context.GoldenRecordMstAudits.Where(x => x.GoldenRecordId == goldenRecordId).FirstOrDefault();
                                if (ga != null)
                                {
                                    g.GoldenRecordStatus = ga.GoldenRecordStatus;
                                    message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREU;
                                }
                            }
                            g.ReviewByDate = null;
                            g.IsAutoStp = false;
                            g.EntryBy = detail.EntryBy;
                            g.EntryDtTime = DateTime.Now;
                            g.EntryDtTimeUtc = DateTime.UtcNow;
                            this.context.SaveChanges();
                            this.linqToDBContext.Update(message);
                            this.linqToDBContext.SaveChanges();
                            res.IsSuccess = true;
                            res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                            res.MessageId = (int)ResponseCodeMessage.Successful;
                            /*
                                As per CR#110 we dont need to send position notifications to client in case of rejected.
                             */
                            //ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                            //Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                        }
                        else
                        {
                            res.IsSuccess = false;
                            res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NotFound);
                            res.MessageId = (int)ResponseCodeMessage.NotFound;
                        }
                    }



                }
                this.SaveInActiveDate(goldenRecordId);
                sendEventNotification(
                     $"Event approval perform",
                     "EVENT_APPROVAL",
                      new
                      {
                          GoldenId = goldenRecordId
                      });

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                res.IsSuccess = false;
                res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InternalServerError);
                res.MessageId = (int)ResponseCodeMessage.InternalServerError;

                dynamic logs = new ExpandoObject();
                logs.Data = JsonConvert.SerializeObject(detail);
                logs.StackTrace = ex.StackTrace;
                logs.goldenId = goldenRecordId;
                logs.status = Status;
                logs.role = Role;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            return res;
        }

        public WorkQueueResponse GetWorkQueueByUserId(int workQueueId, int userId)
        {
            WorkQueueResponse workQueueResponse = new WorkQueueResponse();
            try
            {
                if (userId > 0)
                {
                    workQueueResponse = (from wqm in this.context.WorkQueueMsts
                                         join wqum in this.context.WorkQueueUserMappings on wqm.WorkQueueId equals wqum.WorkQueueId
                                         where ((wqm.WorkQueueId == workQueueId) && (wqum.UserId == userId))
                                         select new WorkQueueResponse()
                                         {
                                             WorkQueueId = (int)wqm.WorkQueueId,
                                             WorkQueueName = wqm.WorkQueueName,
                                             WorkQueueObject = wqm.WorkQueueObject,
                                             EntryBy = wqm.EntryBy,
                                             EntryDtTime = wqm.EntryDtTime,
                                             EntryDtTimeUtc = wqm.EntryDtTimeUtc,
                                             IsActive = wqm.IsActive
                                         }).FirstOrDefault();
                }
                else
                {
                    workQueueResponse = (from wqm in this.context.WorkQueueMsts
                                         where ((wqm.WorkQueueId == workQueueId))
                                         select new WorkQueueResponse()
                                         {
                                             WorkQueueId = (int)wqm.WorkQueueId,
                                             WorkQueueName = wqm.WorkQueueName,
                                             WorkQueueObject = wqm.WorkQueueObject,
                                             EntryBy = wqm.EntryBy,
                                             EntryDtTime = wqm.EntryDtTime,
                                             EntryDtTimeUtc = wqm.EntryDtTimeUtc,
                                             IsActive = wqm.IsActive
                                         }).FirstOrDefault();
                }


                if (workQueueResponse != null)
                {
                    workQueueResponse.IsSuccess = true;
                    workQueueResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                    workQueueResponse.MessageId = (int)ResponseCodeMessage.Successful;
                }
                else
                {
                    workQueueResponse.IsSuccess = true;
                    workQueueResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NoRecord);
                    workQueueResponse.MessageId = (int)ResponseCodeMessage.NoRecord;
                }
            }
            catch (Exception ex)
            {
                workQueueResponse.IsSuccess = false;
                workQueueResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InternalServerError);
                workQueueResponse.MessageId = (int)ResponseCodeMessage.InternalServerError;
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.workQueueId = workQueueId;
                logs.UserId = userId;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }
            return workQueueResponse;
        }


        public List<WorkQueueResponse> GetWorkQueuesByUserId(int userId)
        {
            List<WorkQueueResponse> workQueueResponses = new List<WorkQueueResponse>();
            try
            {
                if (userId > 0)
                {
                    workQueueResponses = (from wqm in this.context.WorkQueueMsts
                                          join wqum in this.context.WorkQueueUserMappings on wqm.WorkQueueId equals wqum.WorkQueueId
                                          where (wqum.UserId == userId)
                                          where (wqm.IsActive == 1)
                                          select new WorkQueueResponse()
                                          {
                                              WorkQueueId = (int)wqm.WorkQueueId,
                                              WorkQueueName = wqm.WorkQueueName,
                                              WorkQueueObject = wqm.WorkQueueObject,
                                              EntryBy = wqm.EntryBy,
                                              EntryDtTime = wqm.EntryDtTime,
                                              EntryDtTimeUtc = wqm.EntryDtTimeUtc,
                                              IsActive = wqm.IsActive
                                          }).ToList();

                }
                else
                {
                    workQueueResponses = (from wqm in this.context.WorkQueueMsts
                                          where (wqm.IsActive == 1)
                                          select new WorkQueueResponse()
                                          {
                                              WorkQueueId = (int)wqm.WorkQueueId,
                                              WorkQueueName = wqm.WorkQueueName,
                                              WorkQueueObject = wqm.WorkQueueObject,
                                              EntryBy = wqm.EntryBy,
                                              EntryDtTime = wqm.EntryDtTime,
                                              EntryDtTimeUtc = wqm.EntryDtTimeUtc,
                                              IsActive = wqm.IsActive
                                          }).ToList();
                }

            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }
            return workQueueResponses;
        }
        public List<WorkQueueResponse> GetWorkQueuesByUserIdAndWqName(int userId, string name)
        {
            List<WorkQueueResponse> workQueueResponses = new List<WorkQueueResponse>();
            try
            {
                if (userId > 0)
                {
                    workQueueResponses = (from wqm in this.context.WorkQueueMsts
                                          join wqum in this.context.WorkQueueUserMappings on wqm.WorkQueueId equals wqum.WorkQueueId
                                          where (wqum.UserId == userId)
                                          where (wqm.IsActive == 1)
                                          where (wqm.WorkQueueName.ToLower() == name.ToLower())
                                          select new WorkQueueResponse()
                                          {
                                              WorkQueueId = (int)wqm.WorkQueueId,
                                              WorkQueueName = wqm.WorkQueueName,
                                              WorkQueueObject = wqm.WorkQueueObject,
                                              EntryBy = wqm.EntryBy,
                                              EntryDtTime = wqm.EntryDtTime,
                                              EntryDtTimeUtc = wqm.EntryDtTimeUtc,
                                              IsActive = wqm.IsActive
                                          }).ToList();

                }
                else
                {
                    workQueueResponses = (from wqm in this.context.WorkQueueMsts
                                          where (wqm.IsActive == 1)
                                          where (wqm.WorkQueueName.ToLower() == name.ToLower())
                                          select new WorkQueueResponse()
                                          {
                                              WorkQueueId = (int)wqm.WorkQueueId,
                                              WorkQueueName = wqm.WorkQueueName,
                                              WorkQueueObject = wqm.WorkQueueObject,
                                              EntryBy = wqm.EntryBy,
                                              EntryDtTime = wqm.EntryDtTime,
                                              EntryDtTimeUtc = wqm.EntryDtTimeUtc,
                                              IsActive = wqm.IsActive
                                          }).ToList();
                }

            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }
            return workQueueResponses;
        }

        public List<CaEventConflict> GetEventConflictDetailsByGoldenId(int goldenId, int userId)
        {
            List<CaEventConflict> conflictDetails = new List<CaEventConflict>();
            GoldenRecordMst g = new GoldenRecordMst();
            try
            {
                conflictDetails = this.context.CaEventConflicts.Where(c => c.GoldenRecordId == goldenId).ToList();
                g = this.context.GoldenRecordMsts.Where(x => x.GoldenRecordId == goldenId).FirstOrDefault();
                if (!conflictDetails.Any(x => x.FieldName.Contains("letterofguaranteeddelivery")) && IsConditional(g))
                {
                    DisplayData displayData = this.linqToDbHelper.GetDisplayDataByGoldenRecordId((int)goldenId);
                    if (displayData.LetterofGuaranteedDelivery == null)
                    {
                        AllSourceResponse sources = GetAllSourceByGoldenId(goldenId);
                        CaEventConflict caEventConflict = new CaEventConflict();
                        caEventConflict.GoldenRecordId = displayData.CaapsId;
                        caEventConflict.FieldName = "eventdetail.letterofguaranteeddelivery.flag";
                        caEventConflict.OptionNumber = "";
                        caEventConflict.MovementId = "";
                        caEventConflict.ExistingSource = sources.Sources?.FirstOrDefault().EventSourceName;
                        caEventConflict.ClientValue = "";
                        caEventConflict.NewEventId = 0;
                        caEventConflict.NewEventSource = "";
                        caEventConflict.NewEventValue = "";
                        caEventConflict.NewEventOptionNumber = "";
                        caEventConflict.NewEventMovementId = "";
                        caEventConflict.IsActive = 1;
                        caEventConflict.EntryBy = userId;
                        caEventConflict.EntryDtTime = DateTime.Now;
                        caEventConflict.EntryDtTimeUtc = DateTime.UtcNow;
                        caEventConflict.FieldStatus = 2;
                        caEventConflict.ReviewStatus = 1;
                        this.context.CaEventConflicts.Add(caEventConflict);
                        displayData.Options.ForEach(option =>
                        {
                            CaEventConflict CaOptCoverExpirationDate = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == goldenId && x.FieldName == "optiondetail.coverexpirationdate.cadatetime" && x.OptionNumber == option.CaOptOptionNumber).FirstOrDefault();
                            CaEventConflict CaOptDepositoryCoverExpirationDate = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == goldenId && x.FieldName == "optiondetail.depositorycoverexpirationdate.cadatetime" && x.OptionNumber == option.CaOptOptionNumber).FirstOrDefault();
                            CaOptCoverExpirationDate.FieldStatus = 0;
                            CaOptCoverExpirationDate.ReviewStatus = 3;
                            CaOptDepositoryCoverExpirationDate.FieldStatus = 0;
                            CaOptDepositoryCoverExpirationDate.ReviewStatus = 3;
                            this.context.CaEventConflicts.Update(CaOptCoverExpirationDate);
                            this.context.CaEventConflicts.Update(CaOptDepositoryCoverExpirationDate);
                            this.context.SaveChanges();
                        });
                        this.context.SaveChanges();
                        conflictDetails = this.context.CaEventConflicts.Where(c => c.GoldenRecordId == goldenId).ToList();
                    }

                    if (g.EventName == "OTHR" && g.EventMvc == "VOLU")
                    {
                        int totalOptions = displayData.Options.Count;
                        if (totalOptions > 0)
                        {
                            for (int i = 0; i < totalOptions; i++)
                            {
                                string currentOptionNumber = displayData.Options[i].CaOptOptionNumber;
                                if (currentOptionNumber != "999")
                                {
                                    if (!conflictDetails.Any(x => x.FieldName.Contains("optiondetail.coverexpirationdate.cadatetime") && x.OptionNumber == currentOptionNumber))
                                    {
                                        AllSourceResponse sources = GetAllSourceByGoldenId(goldenId);
                                        CaEventConflict caEventConflict = new CaEventConflict();
                                        caEventConflict.GoldenRecordId = displayData.CaapsId;
                                        caEventConflict.FieldName = "optiondetail.coverexpirationdate.cadatetime";
                                        caEventConflict.OptionNumber = "";
                                        caEventConflict.MovementId = "";
                                        caEventConflict.ExistingSource = sources.Sources?.FirstOrDefault().EventSourceName;
                                        caEventConflict.ClientValue = "";
                                        caEventConflict.NewEventId = 0;
                                        caEventConflict.NewEventSource = "";
                                        caEventConflict.NewEventValue = "";
                                        caEventConflict.NewEventOptionNumber = "";
                                        caEventConflict.NewEventMovementId = "";
                                        caEventConflict.IsActive = 1;
                                        caEventConflict.EntryBy = userId;
                                        caEventConflict.EntryDtTime = DateTime.Now;
                                        caEventConflict.EntryDtTimeUtc = DateTime.UtcNow;
                                        caEventConflict.FieldStatus = (displayData.LetterofGuaranteedDelivery == "Y" ? 2 : 0);
                                        caEventConflict.ReviewStatus = 1;
                                        this.context.CaEventConflicts.Add(caEventConflict);
                                        this.context.SaveChanges();
                                        conflictDetails = this.context.CaEventConflicts.Where(c => c.GoldenRecordId == goldenId).ToList();
                                    }
                                }
                            }
                        }
                    }
                }
                conflictDetails = this.context.CaEventConflicts.Where(c => c.GoldenRecordId == goldenId).ToList();
            }
            catch (Exception ex)
            {
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.goldenId = goldenId;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            return conflictDetails;
        }

        public List<DisplayData> GetEventsForCounts(int userId, string title, string type)
        {
            List<WorkQueueMst> allWorkQueues = new List<WorkQueueMst>();

            if (userId > 0)
            {
                allWorkQueues = (from wqm in this.context.WorkQueueMsts
                                 join wqum in this.context.WorkQueueUserMappings on wqm.WorkQueueId equals wqum.WorkQueueId
                                 where ((wqum.UserId == userId) && wqm.IsActive == 1)
                                 select new WorkQueueMst()
                                 {
                                     WorkQueueId = (int)wqm.WorkQueueId,
                                     WorkQueueName = wqm.WorkQueueName,
                                     WorkQueueObject = wqm.WorkQueueObject,
                                     EntryBy = wqm.EntryBy,
                                     EntryDtTime = wqm.EntryDtTime,
                                     EntryDtTimeUtc = wqm.EntryDtTimeUtc,
                                     IsActive = wqm.IsActive
                                 }).ToList();
            }
            else
            {
                allWorkQueues = (from wqm in this.context.WorkQueueMsts
                                 where wqm.IsActive == 1
                                 select new WorkQueueMst()
                                 {
                                     WorkQueueId = (int)wqm.WorkQueueId,
                                     WorkQueueName = wqm.WorkQueueName,
                                     WorkQueueObject = wqm.WorkQueueObject,
                                     EntryBy = wqm.EntryBy,
                                     EntryDtTime = wqm.EntryDtTime,
                                     EntryDtTimeUtc = wqm.EntryDtTimeUtc,
                                     IsActive = wqm.IsActive
                                 }).ToList();
            }


            List<DisplayData> displayDatas = this.displayDataHelper.GetAllDisplayData(null, false);
            List<DisplayData> outDisplayData = new List<DisplayData>();
            allWorkQueues.ForEach(queue =>
            {
                try
                {
                    WorkQueueSearch workQueueObject = JsonConvert.DeserializeObject<WorkQueueSearch>(queue.WorkQueueObject);
                    IQueryable<DisplayData> tempDisplays = this.advanceSearchService.GetPredicate(displayDatas.AsQueryable(), workQueueObject);
                    if (tempDisplays is not null)
                    {
                        try
                        {
                            List<DisplayData> ds = tempDisplays.ToList();
                            foreach (var item in ds)
                            {
                                item.GroupName = queue.WorkQueueName;
                                outDisplayData.Add(item);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogException(ex, LogType.Error, ex.StackTrace);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex, LogType.Error, ex.StackTrace);
                    this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
                }
            });

            if (outDisplayData.Count > 0)
            {
                outDisplayData = outDisplayData.Distinct().ToList();
                switch (type)
                {
                    case EventCountType.CRITICAL_COUNT:
                        switch (title)
                        {
                            case EventCountTitle.MY_WORKQUEUE_CATEGORY:
                            case EventCountTitle.All_WORKQUEUE:
                            case EventCountTitle.USER_WORKQUEUE:
                                return outDisplayData.Where(a => a.Priority == (int)EventPriorityEnum.Critical).ToList();
                            case EventCountTitle.MY_PENDED:
                                break;
                        }
                        break;
                    case EventCountType.OLDEST_COUNT:
                        switch (title)
                        {
                            case EventCountTitle.MY_WORKQUEUE_CATEGORY:
                            case EventCountTitle.All_WORKQUEUE:
                            case EventCountTitle.USER_WORKQUEUE:
                                return outDisplayData;
                            case EventCountTitle.MY_PENDED:
                                break;
                        }
                        break;
                    case EventCountType.PENDED_COUNT:
                        switch (title)
                        {
                            case EventCountTitle.MY_WORKQUEUE_CATEGORY:
                            case EventCountTitle.USER_WORKQUEUE:
                                return outDisplayData;
                            case EventCountTitle.MY_PENDED:
                                break;
                        }
                        break;
                    case EventCountType.TOTAL_COUNT:
                        switch (title)
                        {
                            case EventCountTitle.MY_WORKQUEUE_CATEGORY:
                            case EventCountTitle.All_WORKQUEUE:
                            case EventCountTitle.USER_WORKQUEUE:
                                return outDisplayData.ToList();
                            case EventCountTitle.MY_PENDED:
                                break;
                        }
                        break;
                }
            }


            return new List<DisplayData>();
        }

        public EventPublishResponse GetEventPublishedDetails(int goldenId)
        {
            EventPublishResponse publishResponse = new EventPublishResponse();
            try
            {
                var outboundMessagesSwift = (
                      from oms in this.context.OutboundMsts
                      join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                      join osms in this.context.OutboundSwiftRefMsts on oms.DestinationRefId equals osms.OutboundSwiftRefId
                      where (oms.GoldenRecordId == goldenId) &&
                        (oms.IsActive == 1) &&
                        (ams.IsActive == 1) &&
                        (osms.IsActive == 1) &&
                        (oms.Destination == "Swift")
                      select new EventPublishedDetail
                      {
                          BIC = ams.Bic,
                          LegalEntityName = ams.LegalEntityName,
                          //CIBCEntity = ams.CibcEntity,
                          outboundId = oms.OutboundId,
                          DestinationType = oms.Destination,
                          Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                          EventRefId = Convert.ToString(oms.GoldenRecordId),
                          SwiftMessage = "",
                          MessageType = osms.MsgType,
                          ParentCompanyName = ams.ParentCompanyName,
                          SEMERef = osms.SemeRef,
                          EntryDateTimeUtc = oms.EntryDtTimeUtc,
                          IsAutoStp = osms.AutoStp == 1,
                          EventType = "",
                          EmailMsg = "",
                          LegalEntityCdrId = oms.LegalEntityCdrId,
                          IsIgnore = oms.IsIgnore,
                          LinkageEventRef = oms.LinkageEventRef,
                          SendAction = oms.SendAction
                      }).Distinct().ToList();

                //outboundMessagesSwift = outboundMessagesSwift.GroupBy(x => x.SEMERef).Select(x => x.FirstOrDefault()).ToList();

                var outboundMessagesEmail = (
                      from oms in this.context.OutboundMsts
                      join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                      join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                      where (oms.GoldenRecordId == goldenId) &&
                      (oms.IsActive == 1) &&
                      (ams.IsActive == 1) &&
                      (osms.IsActive == 1) &&
                      (oms.Destination == "Email")
                      select new EventPublishedDetail
                      {
                          BIC = ams.Bic,
                          LegalEntityName = ams.LegalEntityName,
                          //CIBCEntity = ams.CibcEntity,
                          outboundId = oms.OutboundId,
                          DestinationType = oms.Destination,
                          Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                          EventRefId = Convert.ToString(oms.GoldenRecordId),
                          SwiftMessage = "",
                          MessageType = osms.MsgType,
                          ParentCompanyName = ams.ParentCompanyName,
                          SEMERef = osms.SemeRef,
                          EntryDateTimeUtc = oms.EntryDtTimeUtc,
                          IsAutoStp = osms.AutoStp == 1,
                          EventType = "",
                          EmailMsg = "",
                          LegalEntityCdrId = oms.LegalEntityCdrId,
                          IsIgnore = oms.IsIgnore,
                          LinkageEventRef = oms.LinkageEventRef,
                          SendAction = oms.SendAction
                      }).Distinct().ToList();

                //outboundMessagesEmail = outboundMessagesEmail.GroupBy(x => x.SEMERef).Select(x => x.FirstOrDefault()).ToList();

                // Under CR#112, a new email flow at the group level has been introduced.
                // GetEventPublishedDetails for each golden ID will now include records where
                // emails have been sent at the group level as well.
                var outboundMessagesEmailByGroup = (
                        from oms in this.context.OutboundMsts
                        join ams in this.context.TradingAccountGroups on oms.GroupId equals ams.Group_Id
                        join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                        where (oms.GoldenRecordId == goldenId) &&
                        (oms.IsActive == 1) &&
                        (ams.Is_Active == 1) &&
                        (osms.IsActive == 1) &&
                        (oms.IsAccountGroup == 1) &&
                        (oms.Destination == "Email")
                        select new EventPublishedDetail
                        {
                            BIC = "",
                            LegalEntityName = "",
                            //CIBCEntity = ams.CibcEntity,
                            outboundId = oms.OutboundId,
                            DestinationType = oms.Destination,
                            Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                            EventRefId = Convert.ToString(oms.GoldenRecordId),
                            SwiftMessage = "",
                            MessageType = osms.MsgType,
                            ParentCompanyName = "",
                            SEMERef = osms.SemeRef,
                            EntryDateTimeUtc = oms.EntryDtTimeUtc,
                            IsAutoStp = osms.AutoStp == 1,
                            EventType = "",
                            EmailMsg = "",
                            LegalEntityCdrId = "",
                            IsIgnore = oms.IsIgnore,
                            LinkageEventRef = oms.LinkageEventRef,
                            SendAction = oms.SendAction,
                            GroupId = oms.GroupId,
                            Group_Name = ams.Group_Name
                        }).Distinct().ToList();

                publishResponse.PublishDetails = outboundMessagesSwift.Union(outboundMessagesEmail).Union(outboundMessagesEmailByGroup).OrderByDescending(x => x.EntryDateTimeUtc).ToList();
                publishResponse.IsSuccess = true;
                publishResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                publishResponse.MessageId = (int)ResponseCodeMessage.Successful;

            }
            catch (Exception ex)
            {
                publishResponse.IsSuccess = false;
                publishResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InternalServerError);
                publishResponse.MessageId = (int)ResponseCodeMessage.InternalServerError;
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }
            return publishResponse;
        }

        public EventPublishResponse GetEventPublishedMsgDetails(long src_outbound_id, string msgType )
        {
            EventPublishResponse publishResponse = new EventPublishResponse();
            try
            {
                
                    List<EventPublishedDetail> temp = new List<EventPublishedDetail>();
                    if (msgType == "Swift")
                    {

                        var outboundMessagesSwift = (
                              from oms in this.context.OutboundMsts
                              join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                              join osms in this.context.OutboundSwiftRefMsts on oms.DestinationRefId equals osms.OutboundSwiftRefId
                              where (oms.OutboundId == src_outbound_id) &&
                                (oms.IsActive == 1) &&
                                (ams.IsActive == 1) &&
                                (osms.IsActive == 1) &&
                                (oms.Destination == "Swift")
                              select new EventPublishedDetail
                              {
                                  BIC = ams.Bic,
                                  LegalEntityName = ams.LegalEntityName,
                                  //CIBCEntity = ams.CibcEntity,
                                  outboundId = oms.OutboundId,
                                  DestinationType = oms.Destination,
                                  Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                                  EventRefId = Convert.ToString(oms.GoldenRecordId),
                                  SwiftMessage = osms.SwiftMsg,
                                  MessageType = osms.MsgType,
                                  ParentCompanyName = ams.ParentCompanyName,
                                  SEMERef = osms.SemeRef,
                                  EntryDateTimeUtc = oms.EntryDtTimeUtc,
                                  IsAutoStp = osms.AutoStp == 1,
                                  EventType = "",
                                  EmailMsg = "",
                                  LegalEntityCdrId = oms.LegalEntityCdrId,
                                  IsIgnore = oms.IsIgnore,
                                  LinkageEventRef = oms.LinkageEventRef,
                                  SendAction = oms.SendAction
                              }).Distinct().ToList();

                           //outboundMessagesSwift = outboundMessagesSwift.GroupBy(x => x.SEMERef).Select(x => x.FirstOrDefault()).ToList();
                           temp = outboundMessagesSwift;
                    }
                    else
                    {

                        var outboundMessagesEmail = (
                              from oms in this.context.OutboundMsts
                              join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                              join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                              where (oms.OutboundId == src_outbound_id) &&
                              (oms.IsActive == 1) &&
                              (ams.IsActive == 1) &&
                              (osms.IsActive == 1) &&
                              (oms.Destination == "Email")
                              select new EventPublishedDetail
                              {
                                  BIC = ams.Bic,
                                  LegalEntityName = ams.LegalEntityName,
                                  //CIBCEntity = ams.CibcEntity,
                                  outboundId = oms.OutboundId,
                                  DestinationType = oms.Destination,
                                  Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                                  EventRefId = Convert.ToString(oms.GoldenRecordId),
                                  SwiftMessage = osms.EmailBody,
                                  MessageType = osms.MsgType,
                                  ParentCompanyName = ams.ParentCompanyName,
                                  SEMERef = osms.SemeRef,
                                  EntryDateTimeUtc = oms.EntryDtTimeUtc,
                                  IsAutoStp = osms.AutoStp == 1,
                                  EventType = "",
                                  EmailMsg = osms.EmailMsg,
                                  LegalEntityCdrId = oms.LegalEntityCdrId,
                                  IsIgnore = oms.IsIgnore,
                                  LinkageEventRef = oms.LinkageEventRef,
                                  SendAction = oms.SendAction
                              }).Distinct().ToList();
                            temp = outboundMessagesEmail;

                    if (temp.IsNullOrEmpty())
                    {
                        var outboundMessagesEmailByGroup = (
                            from oms in this.context.OutboundMsts
                            join ams in this.context.TradingAccountGroups on oms.GroupId equals ams.Group_Id
                            join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                            where (oms.OutboundId == src_outbound_id) &&
                            (oms.IsActive == 1) &&
                            (ams.Is_Active == 1) &&
                            (osms.IsActive == 1) &&
                            (oms.IsAccountGroup == 1) &&
                            (oms.Destination == "Email")
                            select new EventPublishedDetail
                            {
                                BIC = "",
                                LegalEntityName = "",
                                //CIBCEntity = ams.CibcEntity,
                                outboundId = oms.OutboundId,
                                DestinationType = oms.Destination,
                                Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                                EventRefId = Convert.ToString(oms.GoldenRecordId),
                                SwiftMessage = osms.EmailBody,
                                MessageType = osms.MsgType,
                                ParentCompanyName = "",
                                SEMERef = osms.SemeRef,
                                EntryDateTimeUtc = oms.EntryDtTimeUtc,
                                IsAutoStp = osms.AutoStp == 1,
                                EventType = "",
                                EmailMsg = osms.EmailMsg,
                                LegalEntityCdrId = "",
                                IsIgnore = oms.IsIgnore,
                                LinkageEventRef = oms.LinkageEventRef,
                                SendAction = oms.SendAction,
                                GroupId = oms.GroupId,
                                Group_Name = ams.Group_Name
                            }).Distinct().ToList();
                        temp = outboundMessagesEmailByGroup;
                    }
                 }
                publishResponse.PublishDetails = temp.Take(1).ToList();
                publishResponse.IsSuccess = true;
                publishResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                publishResponse.MessageId = (int)ResponseCodeMessage.Successful;
                
            }
            catch (Exception ex)
            {
                publishResponse.IsSuccess = false;
                publishResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InternalServerError);
                publishResponse.MessageId = (int)ResponseCodeMessage.InternalServerError;
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }
            return publishResponse;
        }

        public EventPublishResponse GetAllEventPublishedMsgDetails(int goldenId, DateTime fromDate, DateTime toDate, int isHistoricalPending, Boolean fromEvent)
        {
            EventPublishResponse publishResponse = new EventPublishResponse();
            try
            {
                if(fromEvent)
                {
                    var outboundMessagesSwift = (
                      from oms in this.context.OutboundMsts
                      join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                      join osms in this.context.OutboundSwiftRefMsts on oms.DestinationRefId equals osms.OutboundSwiftRefId
                      where (oms.GoldenRecordId == goldenId) &&
                        (oms.IsActive == 1) &&
                        (ams.IsActive == 1) &&
                        (osms.IsActive == 1) &&
                        (oms.Destination == "Swift")
                      select new EventPublishedDetail
                      {
                          BIC = ams.Bic,
                          LegalEntityName = ams.LegalEntityName,
                          //CIBCEntity = ams.CibcEntity,
                          outboundId = oms.OutboundId,
                          DestinationType = oms.Destination,
                          Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                          EventRefId = Convert.ToString(oms.GoldenRecordId),
                          SwiftMessage = osms.SwiftMsg,
                          MessageType = osms.MsgType,
                          ParentCompanyName = ams.ParentCompanyName,
                          SEMERef = osms.SemeRef,
                          EntryDateTimeUtc = oms.EntryDtTimeUtc,
                          IsAutoStp = osms.AutoStp == 1,
                          EventType = "",
                          EmailMsg = "",
                          LegalEntityCdrId = oms.LegalEntityCdrId,
                          IsIgnore = oms.IsIgnore,
                          LinkageEventRef = oms.LinkageEventRef,
                          SendAction = oms.SendAction
                      }).Distinct().ToList();

                    //outboundMessagesSwift = outboundMessagesSwift.GroupBy(x => x.SEMERef).Select(x => x.FirstOrDefault()).ToList();

                    var outboundMessagesEmail = (
                          from oms in this.context.OutboundMsts
                          join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                          join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                          where (oms.GoldenRecordId == goldenId) &&
                          (oms.IsActive == 1) &&
                          (ams.IsActive == 1) &&
                          (osms.IsActive == 1) &&
                          (oms.Destination == "Email")
                          select new EventPublishedDetail
                          {
                              BIC = ams.Bic,
                              LegalEntityName = ams.LegalEntityName,
                              //CIBCEntity = ams.CibcEntity,
                              outboundId = oms.OutboundId,
                              DestinationType = oms.Destination,
                              Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                              EventRefId = Convert.ToString(oms.GoldenRecordId),
                              SwiftMessage = osms.EmailBody,
                              MessageType = osms.MsgType,
                              ParentCompanyName = ams.ParentCompanyName,
                              SEMERef = osms.SemeRef,
                              EntryDateTimeUtc = oms.EntryDtTimeUtc,
                              IsAutoStp = osms.AutoStp == 1,
                              EventType = "",
                              EmailMsg = osms.EmailMsg,
                              LegalEntityCdrId = oms.LegalEntityCdrId,
                              IsIgnore = oms.IsIgnore,
                              LinkageEventRef = oms.LinkageEventRef,
                              SendAction = oms.SendAction
                          }).Distinct().ToList();

                    // Under CR#112, a new email flow at the group level has been introduced.
                    // GetAllEventPublishedMsgDetails for each golden ID will now include records where
                    // emails have been sent at the group level as well.
                    var outboundMessagesEmailByGroup = (
                        from oms in this.context.OutboundMsts
                        join ams in this.context.TradingAccountGroups on oms.GroupId equals ams.Group_Id
                        join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                        where (oms.GoldenRecordId == goldenId) &&
                        (oms.IsActive == 1) &&
                        (ams.Is_Active == 1) &&
                        (osms.IsActive == 1) &&
                        (oms.IsAccountGroup == 1) &&
                        (oms.Destination == "Email")
                        select new EventPublishedDetail
                        {
                            BIC = "",
                            LegalEntityName = "",
                            //CIBCEntity = ams.CibcEntity,
                            outboundId = oms.OutboundId,
                            DestinationType = oms.Destination,
                            Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                            EventRefId = Convert.ToString(oms.GoldenRecordId),
                            SwiftMessage = osms.EmailBody,
                            MessageType = osms.MsgType,
                            ParentCompanyName = "",
                            SEMERef = osms.SemeRef,
                            EntryDateTimeUtc = oms.EntryDtTimeUtc,
                            IsAutoStp = osms.AutoStp == 1,
                            EventType = "",
                            EmailMsg = osms.EmailMsg,
                            LegalEntityCdrId = "",
                            IsIgnore = oms.IsIgnore,
                            LinkageEventRef = oms.LinkageEventRef,
                            SendAction = oms.SendAction,
                            GroupId = oms.GroupId,
                            Group_Name = ams.Group_Name
                        }).Distinct().ToList();

                    //outboundMessagesEmail = outboundMessagesEmail.GroupBy(x => x.SEMERef).Select(x => x.FirstOrDefault()).ToList();

                    publishResponse.PublishDetails = outboundMessagesSwift.Union(outboundMessagesEmail).Union(outboundMessagesEmailByGroup).OrderByDescending(x => x.EntryDateTimeUtc).ToList();
                    publishResponse.IsSuccess = true;
                    publishResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                    publishResponse.MessageId = (int)ResponseCodeMessage.Successful;
                }
                else
                {
                    List<EventPublishedDetail> outboundMessagesSwift = null;
                    List<EventPublishedDetail> outboundMessagesEmail = null;
                    List<EventPublishedDetail> outboundMessagesEmailByGroup = null;

                    if (isHistoricalPending == 0)
                    {
                        outboundMessagesSwift = (
                              from oms in this.context.OutboundMsts
                              join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                              join osms in this.context.OutboundSwiftRefMsts on oms.DestinationRefId equals osms.OutboundSwiftRefId
                              join grm in this.context.GoldenRecordMsts on oms.GoldenRecordId equals grm.GoldenRecordId
                              where ((oms.EntryDtTimeUtc >= fromDate) &&
                              (oms.EntryDtTimeUtc <= toDate)) &&
                              (oms.IsActive == 1) &&
                              (ams.IsActive == 1) &&
                              (osms.IsActive == 1) &&
                              oms.Destination == "Swift"
                              select new EventPublishedDetail
                              {
                                  BIC = ams.Bic,
                                  LegalEntityName = ams.LegalEntityName,
                                  //CIBCEntity = ams.CibcEntity,
                                  outboundId = oms.OutboundId,
                                  DestinationType = oms.Destination,
                                  Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                                  EventRefId = Convert.ToString(oms.GoldenRecordId),
                                  SwiftMessage = osms.SwiftMsg,
                                  MessageType = osms.MsgType,
                                  ParentCompanyName = ams.ParentCompanyName,
                                  SEMERef = osms.SemeRef,
                                  EntryDateTimeUtc = oms.EntryDtTimeUtc,
                                  IsAutoStp = osms.AutoStp == 1,
                                  EventType = grm.EventName,
                                  EmailMsg = "",
                                  LegalEntityCdrId = oms.LegalEntityCdrId,
                                  IsIgnore = oms.IsIgnore,
                                  LinkageEventRef = oms.LinkageEventRef,
                                  SendAction = oms.SendAction
                              }).Distinct().ToList();

                        //outboundMessagesSwift = outboundMessagesSwift.GroupBy(x => x.SEMERef).Select(x => x.FirstOrDefault()).ToList();

                        outboundMessagesEmail = (
                              from oms in this.context.OutboundMsts
                              join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                              join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                              join grm in this.context.GoldenRecordMsts on oms.GoldenRecordId equals grm.GoldenRecordId
                              where ((oms.EntryDtTimeUtc >= fromDate) &&
                              (oms.EntryDtTimeUtc <= toDate)) &&
                              (oms.IsActive == 1) &&
                              (ams.IsActive == 1) &&
                              (osms.IsActive == 1) &&
                              oms.Destination == "Email"
                              select new EventPublishedDetail
                              {
                                  BIC = ams.Bic,
                                  LegalEntityName = ams.LegalEntityName,
                                  //CIBCEntity = ams.CibcEntity,
                                  outboundId = oms.OutboundId,
                                  DestinationType = oms.Destination,
                                  Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                                  EventRefId = Convert.ToString(oms.GoldenRecordId),
                                  SwiftMessage = osms.EmailBody,
                                  MessageType = osms.MsgType,
                                  ParentCompanyName = ams.ParentCompanyName,
                                  SEMERef = osms.SemeRef,
                                  EntryDateTimeUtc = oms.EntryDtTimeUtc,
                                  IsAutoStp = osms.AutoStp == 1,
                                  EventType = grm.EventName,
                                  EmailMsg = osms.EmailMsg,
                                  LegalEntityCdrId = oms.LegalEntityCdrId,
                                  IsIgnore = oms.IsIgnore,
                                  LinkageEventRef = oms.LinkageEventRef,
                                  SendAction = oms.SendAction
                              }).Distinct().ToList();

                        //outboundMessagesEmail = outboundMessagesEmail.GroupBy(x => x.SEMERef).Select(x => x.FirstOrDefault()).ToList();
                        
                        // Under CR#112, a new email flow at the group level has been introduced.
                        // GetAllEventPublishedMsgDetails for each golden ID will now include records where
                        // emails have been sent at the group level as well.
                        outboundMessagesEmailByGroup = (
                            from oms in this.context.OutboundMsts
                            join ams in this.context.TradingAccountGroups on oms.GroupId equals ams.Group_Id
                            join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                            join grm in this.context.GoldenRecordMsts on oms.GoldenRecordId equals grm.GoldenRecordId
                            where ((oms.EntryDtTimeUtc >= fromDate) &&
                            (oms.EntryDtTimeUtc <= toDate)) &&
                            (oms.IsActive == 1) &&
                            (oms.IsAccountGroup == 1)&&
                            (ams.Is_Active == 1) &&
                            (osms.IsActive == 1) &&
                            oms.Destination == "Email"
                            select new EventPublishedDetail
                            {
                                BIC = "",
                                LegalEntityName = "",
                                //CIBCEntity = ams.CibcEntity,
                                outboundId = oms.OutboundId,
                                DestinationType = oms.Destination,
                                Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                                EventRefId = Convert.ToString(oms.GoldenRecordId),
                                SwiftMessage = osms.EmailBody,
                                MessageType = osms.MsgType,
                                ParentCompanyName = "",
                                SEMERef = osms.SemeRef,
                                EntryDateTimeUtc = oms.EntryDtTimeUtc,
                                IsAutoStp = osms.AutoStp == 1,
                                EventType = grm.EventName,
                                EmailMsg = osms.EmailMsg,
                                LegalEntityCdrId = "",
                                IsIgnore = oms.IsIgnore,
                                LinkageEventRef = oms.LinkageEventRef,
                                SendAction = oms.SendAction,
                                Group_Name = ams.Group_Name,
                                GroupId = ams.Group_Id
                            }).Distinct().ToList();
                    }
                    else
                    {
                        outboundMessagesSwift = (
                              from oms in this.context.OutboundMsts
                              join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                              join osms in this.context.OutboundSwiftRefMsts on oms.DestinationRefId equals osms.OutboundSwiftRefId
                              join grm in this.context.GoldenRecordMsts on oms.GoldenRecordId equals grm.GoldenRecordId
                              where (oms.EntryDtTimeUtc < fromDate) &&
                              (oms.IsActive == 1) &&
                              (ams.IsActive == 1) &&
                              (osms.IsActive == 1) &&
                              oms.Destination == "Swift" &&
                              (oms.OutboundStatus.ToLower() == "swiftnack" || oms.OutboundStatus.ToLower() == "amhnack" || oms.OutboundStatus.ToLower() == "nack")
                              select new EventPublishedDetail
                              {
                                  BIC = ams.Bic,
                                  LegalEntityName = ams.LegalEntityName,
                                  //CIBCEntity = ams.CibcEntity,
                                  outboundId = oms.OutboundId,
                                  DestinationType = oms.Destination,
                                  Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                                  EventRefId = Convert.ToString(oms.GoldenRecordId),
                                  SwiftMessage = osms.SwiftMsg,
                                  MessageType = osms.MsgType,
                                  ParentCompanyName = ams.ParentCompanyName,
                                  SEMERef = osms.SemeRef,
                                  EntryDateTimeUtc = oms.EntryDtTimeUtc,
                                  IsAutoStp = osms.AutoStp == 1,
                                  EventType = grm.EventName,
                                  EmailMsg = "",
                                  LegalEntityCdrId = oms.LegalEntityCdrId,
                                  IsIgnore = oms.IsIgnore,
                                  LinkageEventRef = oms.LinkageEventRef,
                                  SendAction = oms.SendAction
                              }).Distinct().ToList();

                        //outboundMessagesSwift = outboundMessagesSwift.GroupBy(x => x.SEMERef).Select(x => x.FirstOrDefault()).ToList();

                        outboundMessagesEmail = (
                              from oms in this.context.OutboundMsts
                              join ams in this.context.AccountMsts on oms.LegalEntityCdrId equals ams.LegalEntityCdrId
                              join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                              join grm in this.context.GoldenRecordMsts on oms.GoldenRecordId equals grm.GoldenRecordId
                              where (oms.EntryDtTimeUtc < fromDate) &&
                              (oms.IsActive == 1) &&
                              (ams.IsActive == 1) &&
                              (osms.IsActive == 1) &&
                              oms.Destination == "Email" &&
                              (oms.OutboundStatus.ToLower() == "swiftnack" || oms.OutboundStatus.ToLower() == "amhnack" || oms.OutboundStatus.ToLower() == "nack")
                              select new EventPublishedDetail
                              {
                                  BIC = ams.Bic,
                                  LegalEntityName = ams.LegalEntityName,
                                  //CIBCEntity = ams.CibcEntity,
                                  outboundId = oms.OutboundId,
                                  DestinationType = oms.Destination,
                                  Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                                  EventRefId = Convert.ToString(oms.GoldenRecordId),
                                  SwiftMessage = osms.EmailBody,
                                  MessageType = osms.MsgType,
                                  ParentCompanyName = ams.ParentCompanyName,
                                  SEMERef = osms.SemeRef,
                                  EntryDateTimeUtc = oms.EntryDtTimeUtc,
                                  IsAutoStp = osms.AutoStp == 1,
                                  EventType = grm.EventName,
                                  EmailMsg = osms.EmailMsg,
                                  LegalEntityCdrId = oms.LegalEntityCdrId,
                                  IsIgnore = oms.IsIgnore,
                                  LinkageEventRef = oms.LinkageEventRef,
                                  SendAction = oms.SendAction
                              }).Distinct().ToList();

                        // Under CR#112, a new email flow at the group level has been introduced.
                        // GetAllEventPublishedMsgDetails for each golden ID will now include records where
                        // emails have been sent at the group level as well.
                        outboundMessagesEmailByGroup = (
                                from oms in this.context.OutboundMsts
                                join ams in this.context.TradingAccountGroups on oms.GroupId equals ams.Group_Id
                                join osms in this.context.OutboundEmailRefMsts on oms.DestinationRefId equals osms.OutboundEmailRefId
                                join grm in this.context.GoldenRecordMsts on oms.GoldenRecordId equals grm.GoldenRecordId
                                where ((oms.EntryDtTimeUtc >= fromDate) &&
                                (oms.EntryDtTimeUtc <= toDate)) &&
                                (oms.IsActive == 1) &&
                                (oms.IsAccountGroup == 1) &&
                                (ams.Is_Active == 1) &&
                                (osms.IsActive == 1) &&
                                oms.Destination == "Email" &&
                                (oms.OutboundStatus.ToLower() == "swiftnack" || oms.OutboundStatus.ToLower() == "amhnack" || oms.OutboundStatus.ToLower() == "nack")
                                select new EventPublishedDetail
                                {
                                    BIC = "",
                                    LegalEntityName = "",
                                    //CIBCEntity = ams.CibcEntity,
                                    outboundId = oms.OutboundId,
                                    DestinationType = oms.Destination,
                                    Status = oms.OutboundStatus + "," + oms.ErrorMessageText,
                                    EventRefId = Convert.ToString(oms.GoldenRecordId),
                                    SwiftMessage = osms.EmailBody,
                                    MessageType = osms.MsgType,
                                    ParentCompanyName = "",
                                    SEMERef = osms.SemeRef,
                                    EntryDateTimeUtc = oms.EntryDtTimeUtc,
                                    IsAutoStp = osms.AutoStp == 1,
                                    EventType = grm.EventName,
                                    EmailMsg = osms.EmailMsg,
                                    LegalEntityCdrId = "",
                                    IsIgnore = oms.IsIgnore,
                                    LinkageEventRef = oms.LinkageEventRef,
                                    SendAction = oms.SendAction,
                                    Group_Name = ams.Group_Name,
                                    GroupId = ams.Group_Id
                                }).Distinct().ToList();
                    }

                    publishResponse.PublishDetails = outboundMessagesSwift.Union(outboundMessagesEmail).Union(outboundMessagesEmailByGroup).ToList();
                    publishResponse.IsSuccess = true;
                    publishResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                    publishResponse.MessageId = (int)ResponseCodeMessage.Successful;
                }

            }
            catch (Exception ex)
            {
                publishResponse.IsSuccess = false;
                publishResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InternalServerError);
                publishResponse.MessageId = (int)ResponseCodeMessage.InternalServerError;
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }
            return publishResponse;
        
    }
        public List<CaEventMst> GetEvents(int eventId)
        {
            List<CaEventMst> events = new List<CaEventMst>();

            try
            {
                if (eventId == 0)
                {
                    events = this.context.CaEventMsts.Where(x => x.IsActive == 1).ToList();
                }
                else
                {
                    events = this.context.CaEventMsts.Where(c => c.CaEventMstId == eventId).ToList();
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }

            return events;
        }

        public List<CaEventTypeMst> GetEventTypes(int eventTypeId)
        {
            List<CaEventTypeMst> eventTypes = new List<CaEventTypeMst>();

            try
            {
                if (eventTypeId == 0)
                {
                    eventTypes = this.context.CaEventTypeMsts.Where(x => x.IsActive == 1).ToList();
                }
                else
                {
                    eventTypes = this.context.CaEventTypeMsts.Where(c => c.CaEventTypeId == eventTypeId).ToList();
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }

            return eventTypes;
        }
        public List<CaEventEventTypeMapping> GetEventAndEventTypeMapping()
        {
            List<CaEventEventTypeMapping> eventAndEventTypeMapping = new List<CaEventEventTypeMapping>();

            try
            {
                eventAndEventTypeMapping = this.context.CaEventEventTypeMappings.ToList();
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }

            return eventAndEventTypeMapping;
        }
        public EventOptionPositionsWithType GetEventOptionPositions(int goldenId)
        {
            EventOptionPositionsWithType eventOptionPositionsModel = new EventOptionPositionsWithType();

            try
            {
                var positionType = (from grm in this.context.GoldenRecordMsts
                                    join gpd in this.context.CaEventGpdConfigs on grm.GpdDateType equals gpd.GpdDateType
                                    where grm.GoldenRecordId == goldenId
                                    select new
                                    {
                                        GoldenRecordId = grm.GoldenRecordId,
                                        PositionType = gpd.PositionType
                                    }).FirstOrDefault();


                var eventAccounts = (from o in this.context.CaEventElectionOptionsDtls
                                     where (o.GoldenRecordId == goldenId) && o.IsActive == 1
                                     select new
                                     {
                                         TradingAccount = o.TradingAccount,
                                         //Mt564SemeRef = ""
                                     }).ToList();

                // Under CR#112, a new email flow at the group level has been introduced.
                // To ensure the existing email flow in each module remains unaffected,
                // a check for (g.IsAccountGroup == 0) has been added.
                var mt564Accounts = (from o in this.context.OutBoundAccountMappings
                                     join g in this.context.OutboundMsts on o.OutboundId equals g.OutboundId
                                     where (g.GoldenRecordId == goldenId) && o.IsActive == 1 && o.TradingAccountNumber != null 
                                     && g.IsAccountGroup == 0
                                     select new
                                     {
                                         TradingAccount = o.TradingAccountNumber,
                                         //Mt564SemeRef = g.GeneratedEventRef
                                     }).ToList();

                var details = (from o in this.context.CaEventElectionOptionsDtls
                               join a in this.context.CaEventElectionMappings on o.CaEventElectionMappingId equals a.CaEventElectionMappingId
                               where o.IsActive == 1 && o.GoldenRecordId == goldenId
                               select new EventOptionPositionDetailModel
                               {
                                   CaEventElectionOptionsDtlId = o.CaEventElectionOptionsDtlId,
                                   CaEventElectionMappingId = o.CaEventElectionMappingId,
                                   TradingAccount = o.TradingAccount,
                                   OptionNumber = o.OptionNumber,
                                   OptionStatus = o.OptionStatus,
                                   OptionType = o.OptionType,
                                   Currency = o.Currency,
                                   CurrencyId = o.CurrencyId,
                                   Price = o.Price,
                                   Qty = o.Qty,
                                   Rate = o.Rate,
                                   Default = a.IsDefault,
                                   ExternalComment = o.ExternalComment
                               }).ToList();

                if (this.context.GoldenRecordMsts.Where(x => x.GoldenRecordId == goldenId && x.IsActive == 1 && x.IsPositionCaptured == true).Any())
                {
                    var accounts = (from o in this.context.CaEventElectionPositions
                                    where (o.GoldenRecordId == goldenId) && o.IsActive == 1
                                    select new
                                    {
                                        TradingAccount = o.TradingAccountNumber + "-" + o.AccountType,
                                        //Mt564SemeRef = string.Empty

                                    }).ToList();

                    var totalAccounts = accounts.Union(eventAccounts).Union(mt564Accounts).ToList();
                    if (totalAccounts != null)
                    {
                        totalAccounts.ForEach(i =>
                        {
                            string tradingAccnt = i.TradingAccount.ToString();
                            // Under CR#112, a new email flow at the group level has been introduced.
                            // To ensure the existing email flow in each module remains unaffected,
                            // a check for (g.IsAccountGroup == 0) has been added.
                            var query = (from xa in this.context.OutBoundAccountMappings
                                         join g in this.context.OutboundMsts on xa.OutboundId equals g.OutboundId
                                         //join h in this.context.OutboundSwiftRefMsts on g.DestinationRefId equals h.OutboundSwiftRefId
                                         where (g.GoldenRecordId == goldenId) && g.IsAccountGroup == 0 && xa.IsActive == 1 && xa.TradingAccountNumber != null && xa.TradingAccountNumber == tradingAccnt
                                         select new
                                         {
                                             FinalTradingAccount = tradingAccnt,
                                             Mt564SemeRef = g.GeneratedEventRef,
                                             OutboundId = g.OutboundId,
                                             Destination = g.Destination
                                         });

                            var materializedQueryForFinal = query.ToList();

                            var finalAccountsTemp = materializedQueryForFinal.GroupBy(x => x.FinalTradingAccount);

                            Dictionary<string, string> tempObj = new Dictionary<string, string>();
                            finalAccountsTemp.ForEach(x =>
                            {
                                if (x.Any(x => x.Destination == "Email") && x.Any(x => x.Destination == "Swift"))
                                {
                                    tempObj.Add(x.FirstOrDefault().FinalTradingAccount, "isSwift");
                                }
                                else if (x.Any(x => x.Destination == "Swift"))
                                {
                                    tempObj.Add(x.FirstOrDefault().FinalTradingAccount, "isSwift");
                                }
                                else
                                {
                                    tempObj.Add(x.FirstOrDefault().FinalTradingAccount, "isEmail");
                                }
                            });


                            List<FinalAccountsModel> lstObj = new List<FinalAccountsModel>();

                            tempObj.ForEach(tempDict =>
                            {
                                if (tempDict.Value == "isEmail")
                                {
                                    if (tradingAccnt == tempDict.Key)
                                    {
                                        // Under CR#112, a new email flow at the group level has been introduced.
                                        // To ensure the existing email flow in each module remains unaffected,
                                        // a check for (g.IsAccountGroup == 0) has been added.
                                        var query = from x in this.context.OutBoundAccountMappings
                                                    join g in this.context.OutboundMsts on x.OutboundId equals g.OutboundId
                                                    join h in this.context.OutboundEmailRefMsts on g.DestinationRefId equals h.OutboundEmailRefId
                                                    where (g.GoldenRecordId == goldenId) &&
                                                          g.Destination == "Email" &&
                                                          h.MsgType == "564" &&
                                                          x.IsActive == 1 &&
                                                          x.TradingAccountNumber != null &&
                                                          x.TradingAccountNumber == tempDict.Key && 
                                                          g.IsAccountGroup == 0
                                                    select new FinalAccountsModel
                                                    {
                                                        FinalTradingAccount = tradingAccnt,
                                                        Mt564SemeRef = g.GeneratedEventRef,
                                                        OutboundId = g.OutboundId,
                                                        Destination = g.Destination
                                                    };
                                        // Bring the data into memory before applying GroupBy and OrderByDescending
                                        var materializedQuery = query.ToList();
                                        var groupedQuery = materializedQuery.GroupBy(x => x.FinalTradingAccount);
                                        var orderedQuery = groupedQuery.Select(g => g.OrderByDescending(e => e.OutboundId).First());
                                        lstObj.Add(orderedQuery.FirstOrDefault());
                                    }
                                }
                                else
                                {
                                    if (tradingAccnt == tempDict.Key)
                                    {
                                        // Under CR#112, a new email flow at the group level has been introduced.
                                        // To ensure the existing email flow in each module remains unaffected,
                                        // a check for (g.IsAccountGroup == 0) has been added.
                                        var query = from x in this.context.OutBoundAccountMappings
                                                    join g in this.context.OutboundMsts on x.OutboundId equals g.OutboundId
                                                    join h in this.context.OutboundSwiftRefMsts on g.DestinationRefId equals h.OutboundSwiftRefId
                                                    where (g.GoldenRecordId == goldenId) &&
                                                          g.Destination == "Swift" &&
                                                          h.MsgType == "564" &&
                                                          x.IsActive == 1 &&
                                                          x.TradingAccountNumber != null &&
                                                          x.TradingAccountNumber == tempDict.Key &&
                                                          g.IsAccountGroup == 0
                                                    select new FinalAccountsModel
                                                    {
                                                        FinalTradingAccount = tradingAccnt,
                                                        Mt564SemeRef = g.GeneratedEventRef,
                                                        OutboundId = g.OutboundId,
                                                        Destination = g.Destination
                                                    };
                                        // Bring the data into memory before applying GroupBy and OrderByDescending
                                        var materializedQuery = query.ToList();
                                        var groupedQuery = materializedQuery.GroupBy(x => x.FinalTradingAccount);
                                        var orderedQuery = groupedQuery.Select(g => g.OrderByDescending(e => e.OutboundId).First());
                                        lstObj.Add(orderedQuery.FirstOrDefault());
                                    }
                                }
                            });

                            LogUtility.Logger.Logger.Log("Position will calculated Using CaEventElectionPositions", LogType.Debug);
                            LogUtility.Logger.Logger.Log($"Final Account {JsonConvert.SerializeObject(lstObj)}", LogType.Debug);
                            var x = new List<CaapsWebServer.Models.Generic.FinalAccountsModel>();
                            if (lstObj != null && lstObj.Count > 0)
                            {
                                x = lstObj
                                .Where(fa => totalAccounts.Any(t => t.TradingAccount == fa.FinalTradingAccount))
                                .ToList();
                            }
                            var grGroup = this.context.GoldenRecordMsts.Where(g => g.GoldenRecordId == goldenId).ToList();
                            EventOptionPositionsWithType tempResult = new EventOptionPositionsWithType();
                            tempResult.EventOptionPositionsModel = (
                                                                                from o in x.DefaultIfEmpty()
                                                                                from gr in grGroup.DefaultIfEmpty()
                                                                                join a in this.context.CaEventElectionPositions on tradingAccnt equals a.TradingAccountNumber + "-" + a.AccountType into eventElectionPositionGroup
                                                                                from clientPositionDtl in eventElectionPositionGroup.Where(a => a.IsActive == 1 && gr.GoldenRecordId == a.GoldenRecordId).DefaultIfEmpty()
                                                                                join am in this.context.AccountMsts on tradingAccnt.Split('-')[0] equals am.TradingAccountNumber into actMstGroup
                                                                                from actMst in actMstGroup.Where(a => a.IsActive == 1).DefaultIfEmpty()
                                                                                select new EventOptionPositionsModel
                                                                                {
                                                                                    Mt564SemeRef = o?.Mt564SemeRef ?? String.Empty,
                                                                                    TradingAccountNumber = tradingAccnt,
                                                                                    CDR = actMst?.LegalEntityName ?? "",
                                                                                    Position = (clientPositionDtl != null) ? (positionType.PositionType == "TRAD" ? clientPositionDtl.TradeDatePosition : clientPositionDtl.SettleDatePosition) : 0,
                                                                                    TradePosition = clientPositionDtl?.TradeDatePosition ?? 0,
                                                                                    SettlePosition = clientPositionDtl?.SettleDatePosition ?? 0,
                                                                                    TotalElectedPosition = details.Where(c => c.TradingAccount == tradingAccnt && (c.OptionStatus == "INSTRUCTION_ACCEPTED" || c.OptionStatus == "INSTRUCTION_DEFAULT")).Sum(c => c.Qty),
                                                                                    TotalUnElectedPosition = (clientPositionDtl != null) ? ((positionType != null && positionType.PositionType == "TRAD") ? clientPositionDtl.TradeDatePosition : clientPositionDtl.SettleDatePosition) - details.Where(c => c.TradingAccount == tradingAccnt && (c.OptionStatus == "INSTRUCTION_ACCEPTED" || c.OptionStatus == "INSTRUCTION_DEFAULT")).Sum(c => c.Qty) : 0,
                                                                                    PendingPosition = details.Where(c => c.TradingAccount == tradingAccnt && c.OptionStatus == "PENDING_INSTRUCTION").Sum(c => c.Qty),
                                                                                    PendingCancellation = details.Where(c => c.TradingAccount == tradingAccnt && c.OptionStatus == "PENDING_CANCELLATION").Sum(c => c.Qty),
                                                                                    Accounttype = (clientPositionDtl != null) ? (String.IsNullOrEmpty(clientPositionDtl.AccountType) ? "" : this.enumHelper.GetEnumDescription((AccountTypeEnum)Convert.ToInt32(clientPositionDtl.AccountType))) : this.enumHelper.GetEnumDescription((AccountTypeEnum)int.Parse(tradingAccnt.Substring(tradingAccnt.LastIndexOf('-') + 1, tradingAccnt.Length - (tradingAccnt.LastIndexOf('-') + 1)))),
                                                                                    EventOptionPositionDetails = details.Where(c => c.TradingAccount == tradingAccnt).ToList(),
                                                                                    SwiftOrEmail = actMst != null ?
                                                                                     (actMst.SwiftMessageEnabled != null ?

                                                                                        (actMst.SwiftMessageEnabled.ToUpper() == "Y" ?
                                                                                            (actMst.EmailEnabled != null ?
                                                                                                (actMst.EmailEnabled.ToUpper() == "Y" ? "Swift,Email" : "Swift") :
                                                                                                "Swift"
                                                                                            ) :
                                                                                            (actMst.EmailEnabled != null ?
                                                                                                (actMst.EmailEnabled.ToUpper() == "Y" ? "Email" : "none") :
                                                                                                "none"
                                                                                            )
                                                                                        ) :
                                                                                        (actMst.EmailEnabled != null ?
                                                                                            (actMst.EmailEnabled.ToUpper() == "Y" ? "Email" : "none") :
                                                                                            "none"
                                                                                        )
                                                                                    ) : "",
                                                                                    PositionType = positionType?.PositionType,
                                                                                    CIBCEntity = actMst?.CibcEntity
                                                                                }).Where(x => !string.IsNullOrEmpty(x.CDR)).DistinctBy(a => a.TradingAccountNumber).ToList();
                            LogUtility.Logger.Logger.Log("OK1", LogType.Debug);
                            if (tempResult.EventOptionPositionsModel != null)
                            {
                                if (eventOptionPositionsModel.EventOptionPositionsModel == null)
                                {
                                    eventOptionPositionsModel.EventOptionPositionsModel = new List<EventOptionPositionsModel>();
                                }

                                eventOptionPositionsModel.EventOptionPositionsModel.AddRange(tempResult.EventOptionPositionsModel);
                            }
                        });
                    }
                    }
                else
                {
                    var accounts = (from o in this.context.ClientPositionDtls
                                    join g in this.context.GoldenRecordMsts on o.BloombergId equals g.BloombergId
                                    where (g.GoldenRecordId == goldenId) && o.IsActive == 1
                                    select new
                                    {
                                        TradingAccount = o.TradingAccountNumber + "-" + o.Accounttype,
                                    }).ToList();

                    var totalAccounts = accounts.Union(eventAccounts).Union(mt564Accounts).ToList();
                    if (totalAccounts != null)
                    {
                        totalAccounts.ForEach(i =>
                    {
                        string tradingAccnt = i.TradingAccount.ToString();
                        // Under CR#112, a new email flow at the group level has been introduced.
                        // To ensure the existing email flow in each module remains unaffected,
                        // a check for (g.IsAccountGroup == 0) has been added.
                        var query = (from xa in this.context.OutBoundAccountMappings
                                     join g in this.context.OutboundMsts on xa.OutboundId equals g.OutboundId
                                     //join h in this.context.OutboundSwiftRefMsts on g.DestinationRefId equals h.OutboundSwiftRefId
                                     where (g.GoldenRecordId == goldenId) && xa.IsActive == 1 && g.IsAccountGroup == 0 && xa.TradingAccountNumber != null && xa.TradingAccountNumber == tradingAccnt
                                     select new
                                     {
                                         FinalTradingAccount = tradingAccnt,
                                         Mt564SemeRef = g.GeneratedEventRef,
                                         OutboundId = g.OutboundId,
                                         Destination = g.Destination
                                     });

                        var materializedQueryForFinal = query.ToList();

                        var finalAccountsTemp = materializedQueryForFinal.GroupBy(x => x.FinalTradingAccount);

                        Dictionary<string, string> tempObj = new Dictionary<string, string>();
                        finalAccountsTemp.ForEach(x =>
                        {
                            if (x.Any(x => x.Destination == "Email") && x.Any(x => x.Destination == "Swift"))
                            {
                                tempObj.Add(x.FirstOrDefault().FinalTradingAccount, "isSwift");
                            }
                            else if (x.Any(x => x.Destination == "Swift"))
                            {
                                tempObj.Add(x.FirstOrDefault().FinalTradingAccount, "isSwift");
                            }
                            else
                            {
                                tempObj.Add(x.FirstOrDefault().FinalTradingAccount, "isEmail");
                            }
                        });


                        List<FinalAccountsModel> lstObj = new List<FinalAccountsModel>();

                        tempObj.ForEach(tempDict =>
                        {
                            if (tempDict.Value == "isEmail")
                            {
                                if (tradingAccnt == tempDict.Key)
                                {
                                    // Under CR#112, a new email flow at the group level has been introduced.
                                    // To ensure the existing email flow in each module remains unaffected,
                                    // a check for (g.IsAccountGroup == 0) has been added.
                                    var query = from x in this.context.OutBoundAccountMappings
                                                join g in this.context.OutboundMsts on x.OutboundId equals g.OutboundId
                                                join h in this.context.OutboundEmailRefMsts on g.DestinationRefId equals h.OutboundEmailRefId
                                                where (g.GoldenRecordId == goldenId) &&
                                                  g.Destination == "Email" &&
                                                  h.MsgType == "564" &&
                                                  x.IsActive == 1 &&
                                                  x.TradingAccountNumber != null &&
                                                  x.TradingAccountNumber == tempDict.Key && 
                                                  g.IsAccountGroup == 0
                                                select new FinalAccountsModel
                                                {
                                                    FinalTradingAccount = tradingAccnt,
                                                    Mt564SemeRef = g.GeneratedEventRef,
                                                    OutboundId = g.OutboundId,
                                                    Destination = g.Destination
                                                };
                                    // Bring the data into memory before applying GroupBy and OrderByDescending
                                    var materializedQuery = query.ToList();
                                    var groupedQuery = materializedQuery.GroupBy(x => x.FinalTradingAccount);
                                    var orderedQuery = groupedQuery.Select(g => g.OrderByDescending(e => e.OutboundId).First());
                                    lstObj.Add(orderedQuery.FirstOrDefault());
                                }
                            }
                            else
                            {
                                if (tradingAccnt == tempDict.Key)
                                {
                                    // Under CR#112, a new email flow at the group level has been introduced.
                                    // To ensure the existing email flow in each module remains unaffected,
                                    // a check for (g.IsAccountGroup == 0) has been added.
                                    var query = from x in this.context.OutBoundAccountMappings
                                                join g in this.context.OutboundMsts on x.OutboundId equals g.OutboundId
                                                join h in this.context.OutboundSwiftRefMsts on g.DestinationRefId equals h.OutboundSwiftRefId
                                                where (g.GoldenRecordId == goldenId) &&
                                                  g.Destination == "Swift" &&
                                                  h.MsgType == "564" &&
                                                  x.IsActive == 1 &&
                                                  x.TradingAccountNumber != null &&
                                                  x.TradingAccountNumber == tempDict.Key && 
                                                  g.IsAccountGroup == 0
                                                select new FinalAccountsModel
                                                {
                                                    FinalTradingAccount = tradingAccnt,
                                                    Mt564SemeRef = g.GeneratedEventRef,
                                                    OutboundId = g.OutboundId,
                                                    Destination = g.Destination
                                                };

                                    // Bring the data into memory before applying GroupBy and OrderByDescending
                                    var materializedQuery = query.ToList();
                                    var groupedQuery = materializedQuery.GroupBy(x => x.FinalTradingAccount);
                                    var orderedQuery = groupedQuery.Select(g => g.OrderByDescending(e => e.OutboundId).First());
                                    lstObj.Add(orderedQuery.FirstOrDefault());
                                }

                            }
                        });

                        LogUtility.Logger.Logger.Log("Position will calculated Using ClientPositionDtls", LogType.Debug);
                        LogUtility.Logger.Logger.Log($"Final Account {JsonConvert.SerializeObject(lstObj)}", LogType.Debug);
                        var x = new List<CaapsWebServer.Models.Generic.FinalAccountsModel>();
                        if (lstObj != null && lstObj.Count>0)
                        {
                            x = lstObj
                            .Where(fa => totalAccounts.Any(t => t.TradingAccount == fa.FinalTradingAccount))
                            .ToList();
                        }
                        LogUtility.Logger.Logger.Log("lstObj OK", LogType.Debug);
                        var grGroup = this.context.GoldenRecordMsts.Where(g => g.GoldenRecordId == goldenId).ToList();
                        LogUtility.Logger.Logger.Log("grGroup OK", LogType.Debug);
                        EventOptionPositionsWithType tempResult = new EventOptionPositionsWithType();
                    tempResult.EventOptionPositionsModel = (from o in x.DefaultIfEmpty()
                                                            from gr in grGroup.DefaultIfEmpty()
                                                            join a in this.context.ClientPositionDtls on tradingAccnt equals a.TradingAccountNumber + "-" + a.Accounttype into clientPositionDtlGroup
                                                            from clientPositionDtl in clientPositionDtlGroup.Where(a => a.IsActive == 1 && gr.BloombergId == a.BloombergId).DefaultIfEmpty()
                                                            join am in this.context.AccountMsts on tradingAccnt.Split('-')[0] equals am.TradingAccountNumber into actMstGroup
                                                                from actMst in actMstGroup.Where(a => a.IsActive == 1).DefaultIfEmpty()
                                                                select new EventOptionPositionsModel
                                                                {
                                                                    Mt564SemeRef = o?.Mt564SemeRef ?? String.Empty,
                                                                    TradingAccountNumber = tradingAccnt,
                                                                    CDR = actMst?.LegalEntityName ?? "",
                                                                    Position = (clientPositionDtl != null) ? ((positionType != null && positionType.PositionType == "TRAD") ? clientPositionDtl.AggTradeDatePosition : clientPositionDtl.AggSettleDatePosition) : 0,
                                                                    TradePosition = clientPositionDtl?.AggTradeDatePosition ?? 0,
                                                                    SettlePosition = clientPositionDtl?.AggSettleDatePosition ?? 0,
                                                                    TotalElectedPosition = details.Where(c => c.TradingAccount == tradingAccnt && (c.OptionStatus == "INSTRUCTION_ACCEPTED" || c.OptionStatus == "INSTRUCTION_DEFAULT")).Sum(c => c.Qty),
                                                                    TotalUnElectedPosition = (clientPositionDtl != null) ? ((positionType != null && positionType.PositionType == "TRAD") ? clientPositionDtl.AggTradeDatePosition : clientPositionDtl.AggSettleDatePosition) - details.Where(c => c.TradingAccount == tradingAccnt && (c.OptionStatus == "INSTRUCTION_ACCEPTED" || c.OptionStatus == "INSTRUCTION_DEFAULT")).Sum(c => c.Qty) : 0,
                                                                    PendingPosition = details.Where(c => c.TradingAccount == tradingAccnt && c.OptionStatus == "PENDING_INSTRUCTION").Sum(c => c.Qty),
                                                                    PendingCancellation = details.Where(c => c.TradingAccount == tradingAccnt && c.OptionStatus == "PENDING_CANCELLATION").Sum(c => c.Qty),
                                                                    Accounttype = clientPositionDtl?.Accounttype != null ? (String.IsNullOrEmpty(clientPositionDtl.Accounttype) ? "" : this.enumHelper.GetEnumDescription((AccountTypeEnum)Convert.ToInt32(clientPositionDtl.Accounttype))) : this.enumHelper.GetEnumDescription((AccountTypeEnum)int.Parse(tradingAccnt.Substring(tradingAccnt.LastIndexOf('-') + 1, tradingAccnt.Length - (tradingAccnt.LastIndexOf('-') + 1)))),
                                                                    EventOptionPositionDetails = details.Where(c => c.TradingAccount == tradingAccnt).ToList(),
                                                                    SwiftOrEmail = actMst != null ?
                                                                    (actMst.SwiftMessageEnabled != null ?
                                                                        (actMst.SwiftMessageEnabled.ToUpper() == "Y" ?
                                                                            (actMst.EmailEnabled != null ?
                                                                                (actMst.EmailEnabled.ToUpper() == "Y" ? "Swift,Email" : "Swift") :
                                                                                "Swift"
                                                                            ) :
                                                                            (actMst.EmailEnabled != null ?
                                                                                (actMst.EmailEnabled.ToUpper() == "Y" ? "Email" : "none") :
                                                                                "none"
                                                                            )
                                                                        ) :
                                                                        (actMst.EmailEnabled != null ?
                                                                            (actMst.EmailEnabled.ToUpper() == "Y" ? "Email" : "none") :
                                                                            "none"
                                                                        )
                                                                    ) : "",
                                                                    PositionType = positionType?.PositionType,
                                                                    CIBCEntity = actMst?.CibcEntity
                                                                }).Where(x => !string.IsNullOrEmpty(x.CDR)).DistinctBy(a => a.TradingAccountNumber).ToList();
                        LogUtility.Logger.Logger.Log("OK1", LogType.Debug);
                        if (tempResult.EventOptionPositionsModel != null)
                        {
                            if (eventOptionPositionsModel.EventOptionPositionsModel == null)
                            {
                                eventOptionPositionsModel.EventOptionPositionsModel = new List<EventOptionPositionsModel>();
                            }

                            eventOptionPositionsModel.EventOptionPositionsModel.AddRange(tempResult.EventOptionPositionsModel);
                        }

                    });
                    }
                }
                eventOptionPositionsModel.PositionType = positionType.PositionType;

                return eventOptionPositionsModel;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                if (ex.InnerException != null)
                {
                    if (!string.IsNullOrEmpty(ex.InnerException.Message))
                        Logger.LogException(ex, LogType.Error, ex.InnerException.Message);
                }
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }
            return eventOptionPositionsModel;
        }

        public PositionCapturedModel GetPositionCapturedDateByGoldenId(int goldenId)
        {
            var positionCapturedDate = this.context.GoldenRecordMsts.Where(x => x.GoldenRecordId == goldenId && x.IsActive == 1)
                .Select(
                    x => new
                    {
                        x.PositionFixDate,
                        x.IsPositionCaptured
                    }
                    ).FirstOrDefault();

            PositionCapturedModel objPositionCaptured = new PositionCapturedModel
            {
                IsPositionCaptured = positionCapturedDate.IsPositionCaptured,
                PositionCapturedDate = positionCapturedDate.PositionFixDate
            };

            return objPositionCaptured ?? null;
        }

        public EventConflictResolutionParameter UpdateEventConflictResolution(EventConflictResolutionParameter eventConflictResolution, int userId, string Role)
        {
            try
            {
                Logger.Log($"UpdateEventConflictResolution = User ID : {userId} Role : {Role}", LogType.Info);
                bool conditionalFieldChange = false;
                if (Role != UserRoleConsts.SUPERVISOR && Role != UserRoleConsts.ANALYST && Role != UserRoleConsts.SYSTEM_ADMIN)
                {
                    eventConflictResolution.IsSuccess = false;
                    eventConflictResolution.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NoPermission);
                    eventConflictResolution.MessageId = (int)ResponseCodeMessage.NoPermission;
                    return eventConflictResolution;
                }

                if (eventConflictResolution.IsRequired)
                {
                    CaEventConflict eventConflictMst = this.context.CaEventConflicts.Where(x => x.ConflictId == eventConflictResolution.ConflictId).FirstOrDefault();
                    if (eventConflictMst != null)
                    {
                        bool isLeogAndIsDateAvailable = false;
                        bool isDateCodeUpdated = false;
                        DisplayData displayData = this.linqToDbHelper.GetDisplayDataByGoldenRecordId((int)eventConflictMst.GoldenRecordId);
                        if (eventConflictMst.FieldName == "eventdetail.letterofguaranteeddelivery.flag")
                        {
                            if (Newtonsoft.Json.Linq.JObject.
                                Parse(eventConflictResolution.ResolveValue)["flag"] != null &&
                                Newtonsoft.Json.Linq.JObject.
                                Parse(eventConflictResolution.ResolveValue)["flag"].ToString().ToUpper() == "N")
                            {
                                int totalOptions = displayData.Options.Count;
                                if (totalOptions > 0)
                                {
                                    for (int i = 0; i < totalOptions; i++)
                                    {
                                        string currentOptionNumber = displayData.Options[i].CaOptOptionNumber;
                                        if (currentOptionNumber != "999")
                                        {
                                            if (!String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptDepositoryCoverExpirationDate.ToString())
                                                || !String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptCoverExpirationDate.ToString())
                                                || (displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptDepositoryCoverExpirationDateCode == "ONGO")
                                                || (displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptCoverExpirationDateCode == "ONGO")
                                                )
                                            {
                                                isLeogAndIsDateAvailable = true;
                                                displayData.LetterofGuaranteedDelivery = null;
                                                eventConflictResolution.ResolveValue = null;
                                                eventConflictResolution.Comments = null;
                                                conditionalFieldChange = true;
                                                break;
                                            }
                                            else
                                            {
                                                if (displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptDepositoryCoverExpirationDateCode == "UKWN")
                                                {
                                                    displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptDepositoryCoverExpirationDateCode = null;
                                                    isDateCodeUpdated = true;
                                                }
                                                if (displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptCoverExpirationDateCode == "UKWN")
                                                {
                                                    displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptCoverExpirationDateCode = null;
                                                    isDateCodeUpdated = true;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        if (eventConflictMst.FieldName == "optiondetail.withdrawalallowed.flag")
                        {
                            if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictResolution.ResolveValue)["flag"].ToString().ToUpper() == "N")
                            {
                                if (displayData.Options.Where(x => x.CaOptOptionNumber == eventConflictMst.OptionNumber).FirstOrDefault().CaOptRevocabilityPeriodFromDateCode == "UKWN")
                                {
                                    displayData.Options.Where(x => x.CaOptOptionNumber == eventConflictMst.OptionNumber).FirstOrDefault().CaOptRevocabilityPeriodFromDateCode = null;
                                    isDateCodeUpdated = true;
                                }

                                if (displayData.Options.Where(x => x.CaOptOptionNumber == eventConflictMst.OptionNumber).FirstOrDefault().CaOptRevocabilityPeriodToDateCode == "UKWN")
                                {
                                    displayData.Options.Where(x => x.CaOptOptionNumber == eventConflictMst.OptionNumber).FirstOrDefault().CaOptRevocabilityPeriodToDateCode = null;
                                    isDateCodeUpdated = true;
                                }
                            }

                        }

                        eventConflictMst.ResolveBy = userId;
                        eventConflictMst.EntryBy = userId;
                        eventConflictMst.EntryDtTime = DateTime.Now;
                        eventConflictMst.EntryDtTimeUtc = DateTime.UtcNow;
                        eventConflictMst.ResolveType = eventConflictResolution.ResolveType;
                        eventConflictMst.ResolveValue = eventConflictResolution.ResolveValue;
                        eventConflictMst.FieldStatus = isLeogAndIsDateAvailable ? 2 : eventConflictMst.FieldStatus;
                        eventConflictMst.ReviewStatus = isLeogAndIsDateAvailable ? Convert.ToByte(1) : eventConflictResolution.ReviewStatus;
                        eventConflictMst.Comments = eventConflictResolution.Comments;

                        if (eventConflictMst.FieldName == "optiondetail.withdrawalallowed.flag")
                        {
                            CaEventConflict withdrawFrom = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.revocabilityperiod.fromdatetime" && x.OptionNumber == eventConflictMst.OptionNumber).FirstOrDefault();
                            CaEventConflict withdrawTo = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.revocabilityperiod.todatetime" && x.OptionNumber == eventConflictMst.OptionNumber).FirstOrDefault();

                            if (withdrawFrom == null)
                            {
                                CaEventConflict caEventConflict = new CaEventConflict();
                                caEventConflict.GoldenRecordId = eventConflictMst.GoldenRecordId;
                                caEventConflict.FieldName = "optiondetail.revocabilityperiod.fromdatetime";
                                caEventConflict.OptionNumber = eventConflictMst.OptionNumber;
                                caEventConflict.MovementId = string.Empty;
                                caEventConflict.ExistingSource = eventConflictMst.ExistingSource;
                                caEventConflict.ClientValue = "";
                                caEventConflict.NewEventId = 0;
                                caEventConflict.NewEventSource = "";
                                caEventConflict.NewEventValue = "";
                                caEventConflict.NewEventOptionNumber = "";
                                caEventConflict.NewEventMovementId = "";
                                caEventConflict.IsActive = 1;
                                caEventConflict.EntryBy = 9990;
                                caEventConflict.EntryDtTime = DateTime.Now;
                                caEventConflict.EntryDtTimeUtc = DateTime.UtcNow;
                                this.context.CaEventConflicts.Add(caEventConflict);
                                this.context.SaveChanges();
                                withdrawFrom = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.revocabilityperiod.fromdatetime" && x.OptionNumber == eventConflictMst.OptionNumber).FirstOrDefault();
                            }

                            if (withdrawTo == null)
                            {
                                CaEventConflict caEventConflict = new CaEventConflict();
                                caEventConflict.GoldenRecordId = eventConflictMst.GoldenRecordId;
                                caEventConflict.FieldName = "optiondetail.revocabilityperiod.todatetime";
                                caEventConflict.OptionNumber = eventConflictMst.OptionNumber;
                                caEventConflict.MovementId = string.Empty;
                                caEventConflict.ExistingSource = eventConflictMst.ExistingSource;
                                caEventConflict.ClientValue = "";
                                caEventConflict.NewEventId = 0;
                                caEventConflict.NewEventSource = "";
                                caEventConflict.NewEventValue = "";
                                caEventConflict.NewEventOptionNumber = "";
                                caEventConflict.NewEventMovementId = "";
                                caEventConflict.IsActive = 1;
                                caEventConflict.EntryBy = 9990;
                                caEventConflict.EntryDtTime = DateTime.Now;
                                caEventConflict.EntryDtTimeUtc = DateTime.UtcNow;
                                this.context.CaEventConflicts.Add(caEventConflict);
                                this.context.SaveChanges();
                                withdrawTo = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.revocabilityperiod.todatetime" && x.OptionNumber == eventConflictMst.OptionNumber).FirstOrDefault();
                            }

                            if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictResolution.ResolveValue)["flag"].ToString().ToUpper() == "Y")
                            {
                                if (!String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == eventConflictMst.OptionNumber).FirstOrDefault().CaOptRevocabilityPeriodFrom.ToString())
                                    && (withdrawFrom.ReviewStatus == (byte)EventReviewStatus.SystemResolved || withdrawFrom.ReviewStatus == (byte)EventReviewStatus.UserResolved))
                                {
                                    withdrawFrom.FieldStatus = 0;
                                    withdrawFrom.ReviewStatus = 3;
                                }
                                else if (String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == eventConflictMst.OptionNumber).FirstOrDefault().CaOptRevocabilityPeriodFrom.ToString())
                                    && String.IsNullOrEmpty(withdrawFrom.ResolveValue))
                                {
                                    withdrawFrom.ResolveValue = "{\"fromdatecode\":\"UKWN\",\"qualifier\":\"REVO\"}";
                                    withdrawFrom.FieldStatus = 2;
                                    withdrawFrom.ReviewStatus = 2;
                                    conditionalFieldChange = true;
                                }
                                else if (!String.IsNullOrEmpty(withdrawFrom.ResolveValue) && withdrawFrom.ResolveValue.ToLower().Contains("fromdatecode"))
                                {
                                    withdrawFrom.FieldStatus = 2;
                                    withdrawFrom.ReviewStatus = 2;
                                    conditionalFieldChange = true;
                                }
                                else
                                {
                                    withdrawFrom.FieldStatus = 0;
                                    withdrawFrom.ReviewStatus = 3;
                                }


                                if (!String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == eventConflictMst.OptionNumber).FirstOrDefault().CaOptRevocabilityPeriodTo.ToString())
                                    && withdrawTo.ReviewStatus != (byte)EventReviewStatus.InReview)
                                {
                                    withdrawTo.FieldStatus = 0;
                                    withdrawTo.ReviewStatus = 3;
                                }
                                else if (String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == eventConflictMst.OptionNumber).FirstOrDefault().CaOptRevocabilityPeriodTo.ToString())
                                    && String.IsNullOrEmpty(withdrawTo.ResolveValue))
                                {
                                    withdrawTo.FieldStatus = 2;
                                    withdrawTo.ReviewStatus = 1;
                                    conditionalFieldChange = true;
                                }
                                else if (!String.IsNullOrEmpty(withdrawTo.ResolveValue) && withdrawTo.ResolveValue.ToLower().Contains("fromdatecode"))
                                {
                                    withdrawTo.FieldStatus = 2;
                                    withdrawTo.ReviewStatus = 1;
                                    conditionalFieldChange = true;
                                }
                                else
                                {
                                    withdrawTo.FieldStatus = 0;
                                    withdrawTo.ReviewStatus = 3;
                                }
                            }
                            else
                            {
                                withdrawFrom.FieldStatus = 0;
                                withdrawFrom.ReviewStatus = 3;
                                withdrawTo.FieldStatus = 0;
                                withdrawTo.ReviewStatus = 3;
                                withdrawFrom.ResolveValue = null;
                                withdrawFrom.ResolveType = null;
                                withdrawFrom.ResolveBy = null;
                                withdrawFrom.Comments = null;
                                withdrawTo.ResolveValue = null;
                                withdrawTo.ResolveType = null;
                                withdrawTo.ResolveBy = null;
                                withdrawTo.Comments = null;
                            }
                            this.context.CaEventConflicts.Update(withdrawTo);
                            this.context.CaEventConflicts.Update(withdrawFrom);
                            this.context.SaveChanges();
                        }

                        if (eventConflictMst.FieldName == "eventdetail.letterofguaranteeddelivery.flag" && !isLeogAndIsDateAvailable &&
                            Newtonsoft.Json.Linq.JObject.Parse(eventConflictResolution.ResolveValue)["flag"].ToString().ToUpper() != "NA")
                        {
                            int totalOptions = displayData.Options.Count;
                            if (totalOptions > 0)
                            {
                                for (int i = 0; i < totalOptions; i++)
                                {
                                    string currentOptionNumber = displayData.Options[i].CaOptOptionNumber;
                                    if (currentOptionNumber != "999")
                                    {
                                        CaEventConflict CaOptDepositoryCoverExpirationDate = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.depositorycoverexpirationdate.cadatetime" && x.OptionNumber == currentOptionNumber).FirstOrDefault();
                                        CaEventConflict CaOptCoverExpirationDate = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.coverexpirationdate.cadatetime" && x.OptionNumber == currentOptionNumber).FirstOrDefault();

                                        if (CaOptDepositoryCoverExpirationDate == null)
                                        {
                                            CaEventConflict caEventConflict = new CaEventConflict();
                                            caEventConflict.GoldenRecordId = eventConflictMst.GoldenRecordId;
                                            caEventConflict.FieldName = "optiondetail.depositorycoverexpirationdate.cadatetime";
                                            caEventConflict.OptionNumber = currentOptionNumber;
                                            caEventConflict.MovementId = string.Empty;
                                            caEventConflict.ExistingSource = eventConflictMst.ExistingSource;
                                            caEventConflict.ClientValue = "";
                                            caEventConflict.NewEventId = 0;
                                            caEventConflict.NewEventSource = "";
                                            caEventConflict.NewEventValue = "";
                                            caEventConflict.NewEventOptionNumber = "";
                                            caEventConflict.NewEventMovementId = "";
                                            caEventConflict.IsActive = 1;
                                            caEventConflict.EntryBy = 9990;
                                            caEventConflict.EntryDtTime = DateTime.Now;
                                            caEventConflict.EntryDtTimeUtc = DateTime.UtcNow;
                                            this.context.CaEventConflicts.Add(caEventConflict);
                                            this.context.SaveChanges();
                                            CaOptDepositoryCoverExpirationDate = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.depositorycoverexpirationdate.cadatetime" && x.OptionNumber == currentOptionNumber).FirstOrDefault();
                                        }

                                        if (CaOptCoverExpirationDate == null)
                                        {
                                            CaEventConflict caEventConflict = new CaEventConflict();
                                            caEventConflict.GoldenRecordId = eventConflictMst.GoldenRecordId;
                                            caEventConflict.FieldName = "optiondetail.coverexpirationdate.cadatetime";
                                            caEventConflict.OptionNumber = currentOptionNumber;
                                            caEventConflict.MovementId = string.Empty;
                                            caEventConflict.ExistingSource = eventConflictMst.ExistingSource;
                                            caEventConflict.ClientValue = "";
                                            caEventConflict.NewEventId = 0;
                                            caEventConflict.NewEventSource = "";
                                            caEventConflict.NewEventValue = "";
                                            caEventConflict.NewEventOptionNumber = "";
                                            caEventConflict.NewEventMovementId = "";
                                            caEventConflict.IsActive = 1;
                                            caEventConflict.EntryBy = 9990;
                                            caEventConflict.EntryDtTime = DateTime.Now;
                                            caEventConflict.EntryDtTimeUtc = DateTime.UtcNow;
                                            this.context.CaEventConflicts.Add(caEventConflict);
                                            this.context.SaveChanges();
                                            CaOptCoverExpirationDate = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.coverexpirationdate.cadatetime" && x.OptionNumber == currentOptionNumber).FirstOrDefault();
                                        }


                                        if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictResolution.ResolveValue)["flag"].ToString().ToUpper() == "Y")
                                        {
                                            if (!String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptDepositoryCoverExpirationDate.ToString())
                                                && (CaOptDepositoryCoverExpirationDate.ReviewStatus == (byte)EventReviewStatus.SystemResolved || CaOptDepositoryCoverExpirationDate.ReviewStatus == (byte)EventReviewStatus.UserResolved))
                                            {
                                                CaOptDepositoryCoverExpirationDate.FieldStatus = 0;
                                                CaOptDepositoryCoverExpirationDate.ReviewStatus = 3;
                                            }
                                            else if (String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptDepositoryCoverExpirationDate.ToString())
                                                && String.IsNullOrEmpty(CaOptDepositoryCoverExpirationDate.ResolveValue)
                                                && String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptDepositoryCoverExpirationDateCode))
                                            {
                                                CaOptDepositoryCoverExpirationDate.FieldStatus = 2;
                                                CaOptDepositoryCoverExpirationDate.ReviewStatus = 1;
                                                conditionalFieldChange = true;
                                            }
                                            else if (String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptDepositoryCoverExpirationDate.ToString())
                                                && String.IsNullOrEmpty(CaOptDepositoryCoverExpirationDate.ResolveValue)
                                                && (displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptDepositoryCoverExpirationDateCode == "UKWN"))
                                            {
                                                CaOptDepositoryCoverExpirationDate.FieldStatus = 2;
                                                CaOptDepositoryCoverExpirationDate.ReviewStatus = 1;
                                                conditionalFieldChange = true;
                                            }
                                            else if (!String.IsNullOrEmpty(CaOptDepositoryCoverExpirationDate.ResolveValue) &&
                                                CaOptDepositoryCoverExpirationDate.ResolveValue.ToLower().Contains("cadatetime") &&
                                                CaOptDepositoryCoverExpirationDate.ReviewStatus != (byte)EventReviewStatus.InReview)
                                            {
                                                CaOptDepositoryCoverExpirationDate.FieldStatus = 2;
                                                CaOptDepositoryCoverExpirationDate.ReviewStatus = 1;
                                                conditionalFieldChange = true;
                                            }
                                            else if (!String.IsNullOrEmpty(CaOptCoverExpirationDate.ResolveValue) &&
                                              CaOptDepositoryCoverExpirationDate.ResolveValue.ToLower().Contains("cadatetime") &&
                                              CaOptDepositoryCoverExpirationDate.ReviewStatus == (byte)EventReviewStatus.InReview)
                                            {
                                                conditionalFieldChange = true;
                                            }
                                            else
                                            {
                                                CaOptDepositoryCoverExpirationDate.FieldStatus = 0;
                                                CaOptDepositoryCoverExpirationDate.ReviewStatus = 3;
                                            }

                                            if (!String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptCoverExpirationDate.ToString())
                                                && CaOptCoverExpirationDate.ReviewStatus != (byte)EventReviewStatus.InReview)
                                            {
                                                CaOptCoverExpirationDate.FieldStatus = 0;
                                                CaOptCoverExpirationDate.ReviewStatus = 3;
                                            }
                                            else if (String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptCoverExpirationDate.ToString())
                                                && String.IsNullOrEmpty(CaOptCoverExpirationDate.ResolveValue)
                                                && String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptCoverExpirationDateCode))
                                            {
                                                CaOptCoverExpirationDate.FieldStatus = 2;
                                                CaOptCoverExpirationDate.ReviewStatus = 1;
                                                conditionalFieldChange = true;
                                            }
                                            else if (String.IsNullOrEmpty(displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptCoverExpirationDate.ToString())
                                                && String.IsNullOrEmpty(CaOptCoverExpirationDate.ResolveValue)
                                                && (displayData.Options.Where(x => x.CaOptOptionNumber == currentOptionNumber).FirstOrDefault().CaOptCoverExpirationDateCode == "UKWN"))
                                            {
                                                CaOptCoverExpirationDate.FieldStatus = 2;
                                                CaOptCoverExpirationDate.ReviewStatus = 1;
                                                conditionalFieldChange = true;
                                            }
                                            else if (!String.IsNullOrEmpty(CaOptCoverExpirationDate.ResolveValue) &&
                                                CaOptCoverExpirationDate.ResolveValue.ToLower().Contains("cadatetime") &&
                                                CaOptCoverExpirationDate.ReviewStatus != (byte)EventReviewStatus.InReview)
                                            {
                                                CaOptCoverExpirationDate.FieldStatus = 2;
                                                CaOptCoverExpirationDate.ReviewStatus = 1;
                                                conditionalFieldChange = true;
                                            }
                                            else if (!String.IsNullOrEmpty(CaOptCoverExpirationDate.ResolveValue) &&
                                                CaOptCoverExpirationDate.ResolveValue.ToLower().Contains("cadatetime") &&
                                                CaOptCoverExpirationDate.ReviewStatus == (byte)EventReviewStatus.InReview)
                                            {
                                                conditionalFieldChange = true;
                                            }
                                            else
                                            {
                                                CaOptCoverExpirationDate.FieldStatus = 0;
                                                CaOptCoverExpirationDate.ReviewStatus = 3;
                                            }
                                        }
                                        else
                                        {
                                            CaOptDepositoryCoverExpirationDate.FieldStatus = 0;
                                            CaOptDepositoryCoverExpirationDate.ReviewStatus = 3;
                                            CaOptCoverExpirationDate.FieldStatus = 0;
                                            CaOptCoverExpirationDate.ReviewStatus = 3;
                                            CaOptDepositoryCoverExpirationDate.ResolveValue = null;
                                            CaOptDepositoryCoverExpirationDate.ResolveType = null;
                                            CaOptDepositoryCoverExpirationDate.ResolveBy = null;
                                            CaOptDepositoryCoverExpirationDate.Comments = null;
                                            CaOptCoverExpirationDate.ResolveValue = null;
                                            CaOptCoverExpirationDate.ResolveType = null;
                                            CaOptCoverExpirationDate.ResolveBy = null;
                                            CaOptCoverExpirationDate.Comments = null;
                                        }
                                        this.context.CaEventConflicts.Update(CaOptDepositoryCoverExpirationDate);
                                        this.context.CaEventConflicts.Update(CaOptCoverExpirationDate);
                                        this.context.SaveChanges();
                                    }
                                }
                            }
                        }

                        if (IsPeriodField(eventConflictMst.FieldName))
                        {
                            switch (eventConflictMst.FieldName)
                            {
                                case "optiondetail.revocabilityperiod.fromdatetime":
                                    if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["fromdatecode"] != null && Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["fromdatecode"].ToString().ToUpper() == "NA")
                                    {
                                        CaEventConflict objTemp = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.revocabilityperiod.todatetime" && x.OptionNumber == eventConflictMst.OptionNumber).FirstOrDefault();
                                        if (objTemp != null)
                                        {
                                            var finalString = "{\"ToDateCode\":\"NA\",\"qualifier\":\"REVO\"}";
                                            objTemp.ResolveValue = finalString;
                                            objTemp.Comments = eventConflictMst.Comments;
                                            objTemp.ReviewStatus = 2;
                                            this.context.CaEventConflicts.Update(objTemp);
                                        }
                                    }
                                    break;
                                case "optiondetail.revocabilityperiod.todatetime":
                                    if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["ToDateCode"] != null && Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["ToDateCode"].ToString().ToUpper() == "NA")
                                    {
                                        CaEventConflict objTemp = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.revocabilityperiod.fromdatetime" && x.OptionNumber == eventConflictMst.OptionNumber).FirstOrDefault();
                                        if (objTemp != null)
                                        {
                                            var finalString = "{\"fromdatecode\":\"NA\",\"qualifier\":\"REVO\"}";
                                            objTemp.ResolveValue = finalString;
                                            objTemp.Comments = eventConflictMst.Comments;
                                            objTemp.ReviewStatus = 2;
                                            this.context.CaEventConflicts.Update(objTemp);
                                        }
                                    }
                                    break;
                                case "eventdetail.claimperiod.fromdatetime":
                                    if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["fromdatecode"] != null && Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["fromdatecode"].ToString().ToUpper() == "NA")
                                    {
                                        CaEventConflict objTemp = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "eventdetail.claimperiod.todatetime").FirstOrDefault();
                                        if (objTemp != null)
                                        {
                                            var finalString = "{\"ToDateCode\":\"NA\",\"qualifier\":\"CLCP\"}";
                                            objTemp.ResolveValue = finalString;
                                            objTemp.Comments = eventConflictMst.Comments;
                                            objTemp.ReviewStatus = 2;
                                            this.context.CaEventConflicts.Update(objTemp);
                                        }
                                    }
                                    break;
                                case "eventdetail.claimperiod.todatetime":
                                    if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["ToDateCode"] != null && Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["ToDateCode"].ToString().ToUpper() == "NA")
                                    {
                                        CaEventConflict objTemp = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "eventdetail.claimperiod.fromdatetime").FirstOrDefault();
                                        if (objTemp != null)
                                        {
                                            var finalString = "{\"fromdatecode\":\"NA\",\"qualifier\":\"CLCP\"}";
                                            objTemp.ResolveValue = finalString;
                                            objTemp.Comments = eventConflictMst.Comments;
                                            objTemp.ReviewStatus = 2;
                                            this.context.CaEventConflicts.Update(objTemp);
                                        }
                                    }
                                    break;
                                case "optiondetail.periodofaction.fromdatetime":
                                    if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["fromdatecode"] != null && Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["fromdatecode"].ToString().ToUpper() == "NA")
                                    {
                                        CaEventConflict objTemp = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.periodofaction.todatetime" && x.OptionNumber == eventConflictMst.OptionNumber).FirstOrDefault();
                                        if (objTemp != null)
                                        {
                                            var finalString = "{\"ToDateCode\":\"NA\",\"qualifier\":\"PWAL\"}";
                                            objTemp.ResolveValue = finalString;
                                            objTemp.Comments = eventConflictMst.Comments;
                                            objTemp.ReviewStatus = 2;
                                            this.context.CaEventConflicts.Update(objTemp);
                                        }
                                    }
                                    break;
                                case "optiondetail.periodofaction.todatetime":
                                    if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["ToDateCode"] != null && Newtonsoft.Json.Linq.JObject.Parse(eventConflictMst.ResolveValue)["ToDateCode"].ToString().ToUpper() == "NA")
                                    {
                                        CaEventConflict objTemp = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.FieldName == "optiondetail.periodofaction.fromdatetime" && x.OptionNumber == eventConflictMst.OptionNumber).FirstOrDefault();
                                        if (objTemp != null)
                                        {
                                            var finalString = "{\"fromdatecode\":\"NA\",\"qualifier\":\"PWAL\"}";
                                            objTemp.ResolveValue = finalString;
                                            objTemp.Comments = eventConflictMst.Comments;
                                            objTemp.ReviewStatus = 2;
                                            this.context.CaEventConflicts.Update(objTemp);
                                        }
                                    }
                                    break;
                                default:
                                    break;
                            }
                        }

                        if (isLeogAndIsDateAvailable || isDateCodeUpdated)
                        {
                            this.linqToDbHelper.UpdateMT564Message(displayData);
                        }

                        if (eventConflictMst.FieldName == "optiondetail.optionstatus.indicator")
                        {
                            if (Newtonsoft.Json.Linq.JObject.Parse(eventConflictResolution.ResolveValue)["indicator"].ToString().ToUpper() == "CANC")
                            {
                                List<CaEventConflict> allMissingConflicts = this.context.CaEventConflicts.Where(x => x.GoldenRecordId == eventConflictMst.GoldenRecordId && x.OptionNumber == eventConflictMst.OptionNumber && (
                                (x.FieldStatus == Convert.ToInt32(EventFieldStatus.Conflict) ||
                                x.FieldStatus == Convert.ToInt32(EventFieldStatus.Incomplete)) &&
                                x.ReviewStatus == Convert.ToInt32(EventReviewStatus.Open))).ToList();
                                allMissingConflicts.ForEach(missingConflict =>
                                {
                                    missingConflict.ResolveValue = ResolveString(missingConflict.FieldName);
                                    missingConflict.ReviewStatus = Convert.ToByte(EventReviewStatus.InReview);
                                    missingConflict.ResolveType = "CAAPS";
                                    conditionalFieldChange = true;
                                    this.context.CaEventConflicts.Update(missingConflict);
                                });
                            }
                        }
                        /*
                            Redmine : 23933 new_caaps_timezone.
                            Below condition checks if it is datetime field of corporate action options than we add the utc indicator value of given datetime value in conflict status.
                        */
                        if (eventConflictMst.FieldName == "optiondetail.responsedeadlinedate.cadatetime" || eventConflictMst.FieldName == "optiondetail.earlyresponsedeadlinedate.cadatetime" || eventConflictMst.FieldName == "optiondetail.coverexpirationdate.cadatetime" || eventConflictMst.FieldName == "optiondetail.depositorycoverexpirationdate.cadatetime" || eventConflictMst.FieldName == "optiondetail.expirydate.cadatetime" || eventConflictMst.FieldName == "optiondetail.marketdeadlinedate.cadatetime" || eventConflictMst.FieldName == "optiondetail.protectdate.cadatetime")
                        {
                            eventConflictMst.ResolveValue = addUTCIndicator(eventConflictMst.ResolveValue,true);
                        }
                         /*
                            Redmine : 23933 new_caaps_timezone.
                            Below condition checks if it is period field than we add the utc indicator value with static Eastern value.
                        */
                        if(eventConflictMst.FieldName == "optiondetail.revocabilityperiod.todatetime" || eventConflictMst.FieldName == "optiondetail.periodofaction.todatetime")   
                        {
                            eventConflictMst.ResolveValue = addUTCIndicator(eventConflictMst.ResolveValue, false);
                        }
                        this.context.SaveChanges();
                        eventConflictResolution.IsSuccess = true;
                        eventConflictResolution.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Updated);
                        eventConflictResolution.MessageId = (int)ResponseCodeMessage.Updated;
                        Logger.Log($"UpdateEventConflictResolution UpdateWorkFlowStatus 1904 = Golden Record ID : {eventConflictMst.GoldenRecordId}", LogType.Info);
                        this.UpdateWorkFlowStatus(eventConflictMst.GoldenRecordId, Role, userId, conditionalFieldChange);
                        SaveInActiveDate(eventConflictMst.GoldenRecordId);

                        sendEventNotification(
                            $"Conflict Resolved for type: {eventConflictResolution.ResolveType} with value {eventConflictResolution.ResolveValue} ,Resolved by {userId}",
                            "CONFLICT_RESOLVED",
                             new
                             {
                                 GoldenId = eventConflictMst.GoldenRecordId
                             });
                    }
                    else
                    {
                        CaEventConflict caEventConflict = new CaEventConflict();
                        caEventConflict.GoldenRecordId = eventConflictResolution.GoldenRecordId;
                        caEventConflict.FieldName = eventConflictResolution.FieldName;
                        caEventConflict.OptionNumber = eventConflictResolution.OptionNumber;
                        caEventConflict.MovementId = eventConflictResolution.MovementNumber;
                        caEventConflict.ExistingSource = eventConflictResolution.Source;
                        caEventConflict.ClientValue = "";
                        caEventConflict.NewEventId = 0;
                        caEventConflict.NewEventSource = "";
                        caEventConflict.NewEventValue = "";
                        caEventConflict.NewEventOptionNumber = "";
                        caEventConflict.NewEventMovementId = "";
                        caEventConflict.ResolveBy = userId;
                        caEventConflict.ResolveType = eventConflictResolution.ResolveType;
                        caEventConflict.ResolveValue = eventConflictResolution.ResolveValue;
                        caEventConflict.FieldStatus = eventConflictResolution.FieldStatus;
                        caEventConflict.ReviewStatus = eventConflictResolution.ReviewStatus;
                        caEventConflict.Comments = eventConflictResolution.Comments;
                        caEventConflict.IsActive = 1;
                        caEventConflict.EntryBy = 9990;
                        caEventConflict.EntryDtTime = DateTime.Now;
                        caEventConflict.EntryDtTimeUtc = DateTime.UtcNow;
                        this.context.CaEventConflicts.Add(caEventConflict);
                        this.context.SaveChanges();
                        eventConflictResolution.IsSuccess = true;
                        eventConflictResolution.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Updated);
                        eventConflictResolution.MessageId = (int)ResponseCodeMessage.Updated;
                        Logger.Log($"UpdateEventConflictResolution UpdateWorkFlowStatus 1944 = Golden Record ID : {caEventConflict.GoldenRecordId}", LogType.Info);
                        this.UpdateWorkFlowStatus(caEventConflict.GoldenRecordId, Role, userId, conditionalFieldChange);
                    }
                }
                else
                {
                    CaEventConflict eventConflictMst = this.context.CaEventConflicts.Where(x => x.ConflictId == eventConflictResolution.ConflictId).FirstOrDefault();
                    if (eventConflictMst != null)
                    {
                        DisplayData displayData = this.linqToDbHelper.GetDisplayDataByGoldenRecordId((int)eventConflictMst.GoldenRecordId);
                        displayData.Options.ForEach(option =>
                        {
                            if (option.CaOptOptionNumber == eventConflictMst.OptionNumber)
                            {
                                if (eventConflictMst.FieldName == "optiondetail.depositorycoverexpirationdate.cadatetime")
                                {
                                    if (eventConflictResolution.ResolveValue.Contains("cadatetime"))
                                    {
                                        option.CaOptDepositoryCoverExpirationDate = Convert.ToDateTime(Newtonsoft.Json.Linq.JObject.Parse(eventConflictResolution.ResolveValue)["cadatetime"].ToString());
                                    }
                                    if (eventConflictResolution.ResolveValue.Contains("datecode"))
                                    {
                                        option.CaOptDepositoryCoverExpirationDateCode = Newtonsoft.Json.Linq.JObject.Parse(eventConflictResolution.ResolveValue)["datecode"].ToString();
                                    }

                                }
                                else if (eventConflictMst.FieldName == "optiondetail.coverexpirationdate.cadatetime")
                                {
                                    if (eventConflictResolution.ResolveValue.Contains("cadatetime"))
                                    {
                                        option.CaOptCoverExpirationDate = Convert.ToDateTime(Newtonsoft.Json.Linq.JObject.Parse(eventConflictResolution.ResolveValue)["cadatetime"].ToString());
                                    }
                                    if (eventConflictResolution.ResolveValue.Contains("datecode"))
                                    {
                                        option.CaOptCoverExpirationDateCode = Newtonsoft.Json.Linq.JObject.Parse(eventConflictResolution.ResolveValue)["datecode"].ToString();
                                    }
                                }
                            }
                        });
                        this.linqToDbHelper.UpdateMT564Message(displayData);
                        eventConflictMst.EntryBy = userId;
                        eventConflictMst.EntryDtTime = DateTime.Now;
                        eventConflictMst.EntryDtTimeUtc = DateTime.UtcNow;
                        eventConflictMst.FieldStatus = 0;
                        eventConflictMst.ReviewStatus = 3;
                        eventConflictMst.Comments = eventConflictResolution.Comments;
                        this.context.CaEventConflicts.Update(eventConflictMst);
                        this.context.SaveChanges();
                        eventConflictResolution.IsSuccess = true;
                        eventConflictResolution.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Updated);
                        eventConflictResolution.MessageId = (int)ResponseCodeMessage.Updated;
                        Logger.Log($"UpdateEventConflictResolution UpdateWorkFlowStatus 1994 = Golden Record ID : {eventConflictMst.GoldenRecordId}", LogType.Info);
                        this.UpdateWorkFlowStatus(eventConflictMst.GoldenRecordId, Role, userId, conditionalFieldChange);
                        SaveInActiveDate(eventConflictMst.GoldenRecordId);

                        sendEventNotification(
                            $"Conflict Resolved for type: {eventConflictResolution.ResolveType} with value {eventConflictResolution.ResolveValue} ,Resolved by {userId}",
                            "CONFLICT_RESOLVED",
                             new
                             {
                                 GoldenId = eventConflictMst.GoldenRecordId
                             });
                    }
                    else
                    {
                        CaEventConflict caEventConflict = new CaEventConflict();
                        caEventConflict.GoldenRecordId = eventConflictResolution.GoldenRecordId;
                        caEventConflict.FieldName = eventConflictResolution.FieldName;
                        caEventConflict.OptionNumber = eventConflictResolution.OptionNumber;
                        caEventConflict.MovementId = eventConflictResolution.MovementNumber;
                        caEventConflict.ExistingSource = eventConflictResolution.Source;
                        caEventConflict.ClientValue = "";
                        caEventConflict.NewEventId = 0;
                        caEventConflict.NewEventSource = "";
                        caEventConflict.NewEventValue = "";
                        caEventConflict.NewEventOptionNumber = "";
                        caEventConflict.NewEventMovementId = "";
                        caEventConflict.ResolveBy = userId;
                        caEventConflict.ResolveType = eventConflictResolution.ResolveType;
                        caEventConflict.ResolveValue = eventConflictResolution.ResolveValue;
                        caEventConflict.FieldStatus = eventConflictResolution.FieldStatus;
                        caEventConflict.ReviewStatus = eventConflictResolution.ReviewStatus;
                        caEventConflict.Comments = eventConflictResolution.Comments;
                        caEventConflict.IsActive = 1;
                        caEventConflict.EntryBy = 9990;
                        caEventConflict.EntryDtTime = DateTime.Now;
                        caEventConflict.EntryDtTimeUtc = DateTime.UtcNow;
                        this.context.CaEventConflicts.Add(caEventConflict);
                        this.context.SaveChanges();
                        eventConflictResolution.IsSuccess = true;
                        eventConflictResolution.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Updated);
                        eventConflictResolution.MessageId = (int)ResponseCodeMessage.Updated;
                        this.UpdateWorkFlowStatus(caEventConflict.GoldenRecordId, Role, userId, conditionalFieldChange);
                    }
                }
            }
            catch (Exception ex)
            {
                eventConflictResolution.IsSuccess = false;
                eventConflictResolution.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InvalidData);
                eventConflictResolution.MessageId = (int)ResponseCodeMessage.InvalidData;
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }
            return eventConflictResolution;
        }

        public string addUTCIndicator(string resolveValue, bool isCaField)
        {
       
            try
            {
                string jsonString = resolveValue;
                dynamic jsonObject = JObject.Parse(jsonString);
                string cadatetimeString = null;
                if(isCaField)
                {
                    cadatetimeString = jsonObject.cadatetime;
                }else
                {
                    cadatetimeString = jsonObject.ToDateTime;
                }
                DateTime cadatetime;
                string[] formats = { "yyyy-MM-ddTHH:mm:ss.fffZ", "MM/dd/yyyy HH:mm:ss" };
                if (DateTime.TryParseExact(cadatetimeString, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out cadatetime))
                {
                    if(cadatetime.ToString("HHmmss") != "000000")
                    {
                        string indicator = this.linqToDbHelper.UTCHelper(cadatetime);
                        jsonObject.utcindicator = indicator;
                        jsonString = jsonObject.ToString();
                        return jsonString;

                    }
                }
                return resolveValue;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                return resolveValue;
            }
        }

        public void SaveInActiveDate(long goldenRecordId)
        {
            //IRD Logic
            GoldenRecordMst g = this.context.GoldenRecordMsts.Where(x => x.GoldenRecordId == goldenRecordId).FirstOrDefault();
            if (g != null)
            {
                try
                {
                    string matchDateData = System.IO.File.ReadAllText("./MatchDates.json", Encoding.UTF8);
                    var dateField = System.Text.Json.JsonSerializer.Deserialize<List<MatchDateModel>>(matchDateData)
                                                        .Find(a => a.EventName == g.EventName && a.EventMVC == g.EventMvc);

                    CaapsLinqToDB.DBModels.MT564Message message = this.linqToDbHelper.GetEventDetailsByGolderRecordId(goldenRecordId);
                    DateTime? matchDate = this.linqToDbHelper.setInactiveDateHelper(message, dateField.Qualifier);
                    DateTime? responseDeadlineDate = this.linqToDbHelper.setInactiveDateHelper(message, "RDDT");
                    DateTime? earlyResponseDeadlineDate = this.linqToDbHelper.setInactiveDateHelper(message, "EARD");

                    List<DateTime> irdList = new List<DateTime>();
                    irdList.Add(g.ReviewByDate.GetValueOrDefault());
                    irdList.Add(matchDate.GetValueOrDefault());
                    irdList.Add(responseDeadlineDate.GetValueOrDefault());
                    irdList.Add(earlyResponseDeadlineDate.GetValueOrDefault());
                    DateTime? irdDate = irdList.Max();
                    DateTime? prevInactiveDate = g.InactiveDate;

                    bool isOngoingPaymentDate = this.linqToDbHelper.checkDateIsOngoing(message, "PAYD");
                    if (isOngoingPaymentDate && dateField.Qualifier == "PAYD")
                    {
                        irdDate = default(DateTime);
                    }

                    if (irdDate.GetValueOrDefault() != default(DateTime) && irdDate != null)
                    {
                        List<DateTime> latestIrds = new List<DateTime>();
                        latestIrds.Add(irdDate.GetValueOrDefault());
                        latestIrds.Add(DateTime.UtcNow);
                        var inactiveDays = (from cg in this.context.CaEventConfigs
                                            join cem in this.context.CaEventTypeMsts on cg.CaEventTypeId equals cem.CaEventTypeId
                                            join ce in this.context.CaEventMsts on cg.CaEventMstId equals ce.CaEventMstId
                                            where ce.CaEventCode == g.EventName && cem.CaEventType == g.EventMvc
                                            select cg.InactiveDays).FirstOrDefault();
                        g.InactiveDate = latestIrds.Max().AddDays(inactiveDays).Date;
                        g.IsActive = 1;
                    }
                    else
                    {
                        g.InactiveDate = g.GoldenRecordCreateDtTimeUtc.GetValueOrDefault().AddDays(365).Date;
                    }

                    if(prevInactiveDate != null && prevInactiveDate > g.InactiveDate)
                    {
                        g.InactiveDate = prevInactiveDate;
                    }

                    this.context.SaveChanges();
                }
                catch (Exception ex)
                {
                    dynamic logs = new ExpandoObject();
                    logs.StackTrace = ex.StackTrace;
                    Logger.LogException(ex, LogType.Error, logs);
                    this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
                }
            }
            //IRD Logic end
        }



        public void sendEventNotification(string message, string action, dynamic additionalDetails)
        {
            NotificationModel notificationModel = new NotificationModel();
            notificationModel.GUID = Guid.NewGuid().ToString();
            notificationModel.DateTime = DateTime.UtcNow;
            notificationModel.Message = message; //;
            notificationModel.Link = string.Empty;
            notificationModel.Action = action;// ;
            notificationModel.AdditionalDetails = additionalDetails;
            notificationModel.Severity = "INFORMATION";
            notificationModel.Priority = "NORMAL";
            notificationModel.Component = "WEB_SERVER";
            notificationModel.Type = "APP";
            notificationModel.UpdateUI = true;
            this.solaceMessageListener.SendToTopic(this.solaceSettings.ListnerTopicName, JsonConvert.SerializeObject(notificationModel));
        }


        public void UpdateWorkFlowStatus(long golderRecordId, string Role, int userId, bool conditionalFieldChange = false,bool fromAddOrCloneOptionOrMovementAction = false)
        {
            try
            {
                Logger.Log($"UpdateWorkFlowStatus Golden Record ID : {golderRecordId} Role : {Role} User ID : {userId} Conditional Field Change : {conditionalFieldChange}", LogType.Info);

                List<CaEventConflict> conflicts = this.context.CaEventConflicts.Where(c => c.GoldenRecordId == golderRecordId).ToList();

                CaEventConflict c = conflicts.Find(x => (x.FieldStatus == (int)EventFieldStatus.Incomplete || x.FieldStatus == (int)EventFieldStatus.Conflict) && x.ReviewStatus == (int)EventReviewStatus.Open);

                if (c == null)
                {
                    GoldenRecordMst g = this.context.GoldenRecordMsts.Where(gr => gr.GoldenRecordId == golderRecordId).FirstOrDefault();
                    CaapsLinqToDB.DBModels.MT564Message message = this.linqToDbHelper.GetEventDetailsByGolderRecordId((long)golderRecordId);
                    if (g != null)
                    {
                        if (conditionalFieldChange)
                        {
                            //if (g.GoldenRecordStatus != EventWorkFlowStatus.Complete && g.GoldenRecordStatus != EventWorkFlowStatus.PendingComplete)
                            //{
                            //    g.GoldenRecordStatus = EventWorkFlowStatus.PendingComplete;
                            //    g.EntryBy = userId;
                            //    g.IsAutoStp = false;
                            //    g.ReviewByDate = null;
                            //    g.EntryDtTime = DateTime.Now;
                            //    g.EntryDtTimeUtc = DateTime.UtcNow;
                            //    this.context.SaveChanges();
                            //    message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREC;
                            //    this.linqToDBContext.Update(message);
                            //    this.linqToDBContext.SaveChanges();
                            //    ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                            //    Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                            //}
                            if (conflicts.Any(x => x.ReviewStatus == (int)EventReviewStatus.InReview))
                            {
                                if (g.GoldenRecordStatus != EventWorkFlowStatus.PendingComplete)
                                {
                                    Logger.Log($"UpdateWorkFlowStatus EventWorkFlowStatus.PendingComplete", LogType.Info);

                                    g.GoldenRecordStatus = EventWorkFlowStatus.PendingComplete;
                                    g.EntryBy = userId;
                                    g.IsAutoStp = false;
                                    g.ReviewByDate = null;
                                    g.EntryDtTime = DateTime.Now;
                                    g.EntryDtTimeUtc = DateTime.UtcNow;
                                    this.context.SaveChanges();
                                    message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREC;
                                    this.linqToDBContext.Update(message);
                                    this.linqToDBContext.SaveChanges();
                                    ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                                    Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                                }
                            }
                            else
                            {
                                //Under CR#119 for add/clone option/movement the event workflow status should change to pending complete.
                                //And whereever we are making event direct complete their needs to add check of pending complete.
                                if (g.GoldenRecordStatus != EventWorkFlowStatus.Complete && g.GoldenRecordStatus != EventWorkFlowStatus.PendingComplete)
                                {
                                    Logger.Log($"UpdateWorkFlowStatus EventWorkFlowStatus.Complete", LogType.Info);
                                    g.GoldenRecordStatus = EventWorkFlowStatus.Complete;
                                    g.EntryBy = userId;
                                    g.IsAutoStp = false;
                                    g.ReviewByDate = null;
                                    g.EntryDtTime = DateTime.Now;
                                    g.EntryDtTimeUtc = DateTime.UtcNow;
                                    this.context.SaveChanges();
                                    message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREC;
                                    this.linqToDBContext.Update(message);
                                    this.linqToDBContext.SaveChanges();
                                    ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                                    Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                                }

                                if(fromAddOrCloneOptionOrMovementAction)
                                {
                                    g = this.context.GoldenRecordMsts.Where(gr => gr.GoldenRecordId == golderRecordId).FirstOrDefault();
                                    if(g.GoldenRecordStatus == EventWorkFlowStatus.Complete)
                                    {
                                        Logger.Log($"UpdateWorkFlowStatus EventWorkFlowStatus.PendingComplete", LogType.Info);
                                        g.GoldenRecordStatus = EventWorkFlowStatus.PendingComplete;
                                        g.EntryBy = userId;
                                        g.IsAutoStp = false;
                                        g.ReviewByDate = null;
                                        g.EntryDtTime = DateTime.Now;
                                        g.EntryDtTimeUtc = DateTime.UtcNow;
                                        this.context.SaveChanges();
                                        message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREC;
                                        this.linqToDBContext.Update(message);
                                        this.linqToDBContext.SaveChanges();
                                        ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                                        Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                                    }
                                }
                            }
                            //g.EntryBy = userId;
                            //g.IsAutoStp = false;
                            //g.ReviewByDate = null;
                            //g.EntryDtTime = DateTime.Now;
                            //g.EntryDtTimeUtc = DateTime.UtcNow;
                            //this.context.SaveChanges();
                            //message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREC;
                            //this.linqToDBContext.Update(message);
                            //this.linqToDBContext.SaveChanges();
                            //ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                            //Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                        }
                        else
                        {
                            Logger.Log($"UpdateWorkFlowStatus EventWorkFlowStatus.PendingComplete Conditional Field Change : {conditionalFieldChange}", LogType.Info);
                            g.GoldenRecordStatus = EventWorkFlowStatus.PendingComplete;
                            g.EntryBy = userId;
                            g.IsAutoStp = false;
                            g.ReviewByDate = null;
                            g.EntryDtTime = DateTime.Now;
                            g.EntryDtTimeUtc = DateTime.UtcNow;
                            this.context.SaveChanges();
                            message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREC;
                            this.linqToDBContext.Update(message);
                            this.linqToDBContext.SaveChanges();
                            ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                            Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                        }
                    }
                }
                else
                {
                    if (conditionalFieldChange)
                    {
                        GoldenRecordMst g = this.context.GoldenRecordMsts.Where(gr => gr.GoldenRecordId == golderRecordId).FirstOrDefault();
                        CaapsLinqToDB.DBModels.MT564Message message = this.linqToDbHelper.GetEventDetailsByGolderRecordId((long)golderRecordId);
                        if (g != null)
                        {
                            if (g.GoldenRecordStatus == EventWorkFlowStatus.Complete || g.GoldenRecordStatus == EventWorkFlowStatus.PendingComplete)
                            {
                                Logger.Log($"UpdateWorkFlowStatus EventWorkFlowStatus.Incomplete Conditional Field Change : {conditionalFieldChange}", LogType.Info);
                                g.GoldenRecordStatus = EventWorkFlowStatus.Incomplete;
                                g.EntryBy = userId;
                                g.IsAutoStp = false;
                                g.ReviewByDate = null;
                                g.EntryDtTime = DateTime.Now;
                                g.EntryDtTimeUtc = DateTime.UtcNow;
                                this.context.SaveChanges();
                                message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREC;
                                this.linqToDBContext.Update(message);
                                this.linqToDBContext.SaveChanges();
                                ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, g);
                                Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                            }
                        }
                    }


                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);

            }
        }
        public EventReviewDateParameter UpdateEventReviewDate(EventReviewDateParameter eventDetail, int userId)
        {
            try
            {
                GoldenRecordMst goldenRecord = this.context.GoldenRecordMsts.Where(a => a.GoldenRecordId == eventDetail.GoldenRecordID).FirstOrDefault();
                CaapsLinqToDB.DBModels.MT564Message message = this.linqToDbHelper.GetEventDetailsByGolderRecordId((long)eventDetail.GoldenRecordID);
                if (goldenRecord != null)
                {
                    goldenRecord.EntryBy = userId;
                    goldenRecord.EntryDtTime = DateTime.Now;
                    goldenRecord.EntryDtTimeUtc = DateTime.UtcNow;
                    goldenRecord.ReviewByDate = eventDetail.ReviewByDate;
                    int statusUpdate = 0;
                    if (goldenRecord.GoldenRecordStatus != EventWorkFlowStatus.Complete)
                    {
                        statusUpdate = 1;
                        Logger.Log($"UpdateEventReviewDate EventWorkFlowStatus.Conditional", LogType.Info);
                        goldenRecord.GoldenRecordStatus = EventWorkFlowStatus.Conditional;
                        goldenRecord.IsAutoStp = false;
                        message.MessageData.GeneralInformation.StatusCode = EventProcessingStatus.PREC;
                    }
                    this.context.SaveChanges();
                    this.linqToDBContext.Update(message);
                    this.linqToDBContext.SaveChanges();
                    eventDetail.IsSuccess = true;
                    eventDetail.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Updated);
                    eventDetail.MessageId = (int)ResponseCodeMessage.Updated;
                    if (statusUpdate == 1)
                    {
                        ResponseParent solaceMessage = this.SendSolaceMessageLinqDB(solaceSettings.PositionCheckQueueName, message, goldenRecord);
                        Logger.Log("Solace Message sending = " + solaceMessage.IsSuccess, LogType.Debug);
                    }
                    SaveInActiveDate(eventDetail.GoldenRecordID);
                    sendEventNotification(
                    $"Review Date Updated",
                    "EVENT_APPROVAL",
                    new
                    {
                        GoldenId = eventDetail.GoldenRecordID
                    });
                }
                else
                {
                    eventDetail.IsSuccess = false;
                    eventDetail.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NotFound);
                    eventDetail.MessageId = (int)ResponseCodeMessage.NotFound;
                }
            }
            catch (Exception ex)
            {
                eventDetail.IsSuccess = false;
                eventDetail.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InvalidData);
                eventDetail.MessageId = (int)ResponseCodeMessage.InvalidData;
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.EventReviewDetail = JsonConvert.SerializeObject(eventDetail);
                logs.userId = userId;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            return eventDetail;
        }


        public EventInActiveDateParameter UpdateEventInActiveDate(EventInActiveDateParameter eventDetail, int userId)
        {
            try
            {
                GoldenRecordMst goldenRecord = this.context.GoldenRecordMsts.Where(a => a.GoldenRecordId == eventDetail.GoldenRecordID).FirstOrDefault();
                if (goldenRecord != null)
                {
                    goldenRecord.EntryBy = userId;
                    goldenRecord.EntryDtTime = DateTime.Now;
                    goldenRecord.EntryDtTimeUtc = DateTime.UtcNow;
                    if (eventDetail.InActiveDate.Date >= DateTime.UtcNow.Date)
                    {
                        goldenRecord.InactiveDate = eventDetail.InActiveDate;
                        goldenRecord.IsActive = 1;
                        eventDetail.IsSuccess = true;
                        eventDetail.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Updated);
                        eventDetail.MessageId = (int)ResponseCodeMessage.Updated;
                        this.context.SaveChanges();
                    }
                    else
                    {
                        eventDetail.IsSuccess = false;
                        eventDetail.Message = "inactive date can not be less than today.";
                        eventDetail.MessageId = (int)ResponseCodeMessage.NotFound;
                    }
                }
                else
                {
                    eventDetail.IsSuccess = false;
                    eventDetail.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NotFound);
                    eventDetail.MessageId = (int)ResponseCodeMessage.NotFound;
                }
            }
            catch (Exception ex)
            {
                eventDetail.IsSuccess = false;
                eventDetail.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InvalidData);
                eventDetail.MessageId = (int)ResponseCodeMessage.InvalidData;
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.EventReviewDetail = JsonConvert.SerializeObject(eventDetail);
                logs.userId = userId;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            return eventDetail;
        }

        public EventReceivedResponse GetEventReceivedResponse(int goldenId)
        {
            EventReceivedResponse eventReceivedResponse = new EventReceivedResponse();
            try
            {
                var receivedMessages = (
                    from grcem in this.context.GoldenRecordSourceEventMappings
                    join rced in this.context.ReceivedCaEventDtls on Convert.ToInt32(grcem.ReceivedId) equals rced.ReceivedCaEventDtlId
                    where (grcem.GoldenRecordId == goldenId) && (grcem.IsActive == 1)
                    select new EventReceivedDetail
                    {
                        GoldenRecordSourceEventMappingId = grcem.GoldenRecordSourceEventMappingId,
                        GoldenRecordId = grcem.GoldenRecordId,
                        SourceName = (rced.FileImportMstId == -2) ? this.context.UserMsts.Where(x => x.UserId == Convert.ToInt32(grcem.SourceName)).FirstOrDefault().FullName : grcem.SourceName,
                        SourceIdentifier = null,
                        CORPId = grcem.CorpId,
                        ReceivedId = Convert.ToInt32(grcem.ReceivedId),
                        CAEventForDate = rced.CaEventForDate,
                        ExtractedMessage = rced.ExtractedMessage,
                        BloomberId = rced.BloombergId,
                        Cusip = rced.Cusip,
                        ISIN = rced.Isin,
                        SEDOL = rced.Sedol,
                        IsProcessed = rced.IsProcessed,
                        Message = (rced.FileImportMstId == -2) ? "Manual" : (grcem.MessageType == null ? "MT564" : (grcem.MessageType == string.Empty ? "MT564" : grcem.MessageType)),
                        EntryDateTimeUtc = grcem.EntryDtTimeUtc,
                        IsManual = (rced.FileImportMstId == -2)
                    }).ToList();

                receivedMessages.AddRange(
                    (from grcem in this.context.GoldenRecordSourceEventMappingAudits
                     join rced in this.context.ReceivedCaEventDtls on Convert.ToInt32(grcem.ReceivedId) equals rced.ReceivedCaEventDtlId
                     where (grcem.GoldenRecordId == goldenId) && (grcem.IsActive == 1)
                     select new EventReceivedDetail
                     {
                         GoldenRecordSourceEventMappingId = (long)grcem.GoldenRecordSourceEventMappingId,
                         GoldenRecordId = (long)grcem.GoldenRecordId,
                         SourceName = (rced.FileImportMstId == -2) ? this.context.UserMsts.Where(x => x.UserId == Convert.ToInt32(grcem.SourceName)).FirstOrDefault().FullName : grcem.SourceName,
                         SourceIdentifier = null,
                         CORPId = grcem.CorpId,
                         ReceivedId = Convert.ToInt32(grcem.ReceivedId),
                         CAEventForDate = rced.CaEventForDate,
                         ExtractedMessage = rced.ExtractedMessage,
                         BloomberId = rced.BloombergId,
                         Cusip = rced.Cusip,
                         ISIN = rced.Isin,
                         SEDOL = rced.Sedol,
                         IsProcessed = rced.IsProcessed,
                         Message = (rced.FileImportMstId == -2) ? "Manual" : (grcem.MessageType == null ? "MT564" : (grcem.MessageType == string.Empty ? "MT564" : grcem.MessageType)),
                         EntryDateTimeUtc = (DateTime)grcem.EntryDtTimeUtc,
                         IsManual = (rced.FileImportMstId == -2)
                     }).ToList()
                    );

                receivedMessages.AddRange(
                    (from grcem in this.context.GoldenRecordSourceEventMt568Mapping
                     join rced in this.context.ReceivedCaEventDtls on Convert.ToInt32(grcem.ReceivedId) equals rced.ReceivedCaEventDtlId
                     where (grcem.GoldenRecordId == goldenId) && (grcem.IsActive == 1)
                     select new EventReceivedDetail
                     {
                         GoldenRecordSourceEventMappingId = grcem.GoldenRecordSourceEventMt568MappingId,
                         GoldenRecordId = grcem.GoldenRecordId,
                         SourceName = grcem.SourceName,
                         SourceIdentifier = null,
                         CORPId = grcem.CorpId,
                         ReceivedId = Convert.ToInt32(grcem.ReceivedId),
                         CAEventForDate = rced.CaEventForDate,
                         ExtractedMessage = rced.ExtractedMessage,
                         BloomberId = rced.BloombergId,
                         Cusip = rced.Cusip,
                         ISIN = rced.Isin,
                         SEDOL = rced.Sedol,
                         IsProcessed = rced.IsProcessed,
                         Message = "MT568",
                         EntryDateTimeUtc = grcem.EntryDtTimeUtc,
                         IsManual = (rced.FileImportMstId == -2)
                     }).ToList()
                    );

                receivedMessages.AddRange(
                    (from grcem in this.context.GoldenRecordSourceEventMt568MappingAudit
                     join rced in this.context.ReceivedCaEventDtls on Convert.ToInt32(grcem.ReceivedId) equals rced.ReceivedCaEventDtlId
                     where (grcem.GoldenRecordId == goldenId) && (grcem.IsActive == 1)
                     select new EventReceivedDetail
                     {
                         GoldenRecordSourceEventMappingId = (long)grcem.GoldenRecordSourceEventMt568MappingId,
                         GoldenRecordId = (long)grcem.GoldenRecordId,
                         SourceName = grcem.SourceName,
                         SourceIdentifier = null,
                         CORPId = grcem.CorpId,
                         ReceivedId = Convert.ToInt32(grcem.ReceivedId),
                         CAEventForDate = rced.CaEventForDate,
                         ExtractedMessage = rced.ExtractedMessage,
                         BloomberId = rced.BloombergId,
                         Cusip = rced.Cusip,
                         ISIN = rced.Isin,
                         SEDOL = rced.Sedol,
                         IsProcessed = rced.IsProcessed,
                         Message = "MT568",
                         EntryDateTimeUtc = (DateTime)grcem.EntryDtTimeUtc,
                         IsManual = (rced.FileImportMstId == -2)
                     }).ToList()
                    );

                eventReceivedResponse.ReceivedDetails = receivedMessages.OrderByDescending(x => x.EntryDateTimeUtc).ToList();
                eventReceivedResponse.IsSuccess = true;
                eventReceivedResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                eventReceivedResponse.MessageId = (int)ResponseCodeMessage.Successful;
            }
            catch (Exception ex)
            {
                eventReceivedResponse.IsSuccess = false;
                eventReceivedResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InternalServerError);
                eventReceivedResponse.MessageId = (int)ResponseCodeMessage.InternalServerError;
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.goldenRecordId = goldenId;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            return eventReceivedResponse;
        }

        public OptionsLinkResponse GetLinkOptionsByGoldenId(int goldenId)
        {
            OptionsLinkResponse optionsLinkResponse = new OptionsLinkResponse();
            try
            {

                List<OptionLinkDbResult> optionLinksDb = (from o in this.context.CaOptionLinks
                                                          join a in this.context.GoldenRecordSourceEventMappings on o.SourceEventId equals a.SemeRef into grOptionLink
                                                          from oLink in grOptionLink.DefaultIfEmpty()
                                                          where (o.GoldenRecordId == goldenId) && (o.IsActive == 1)
                                                          select new OptionLinkDbResult
                                                          {
                                                              CaOptionLinkId = o.CaOptionLinkId,
                                                              GoldenRecordId = o.GoldenRecordId,
                                                              ClientLinkStatus = o.ClientLinkStatus,
                                                              ClientOptionNumber = o.ClientOptionNumber,
                                                              ClientOptionStatus = o.ClientOptionStatus,
                                                              SourceName = o.SourceName,
                                                              SourceBic = o.SourceBic,
                                                              SourceEventId = o.SourceEventId,
                                                              SourceOptionNumber = o.SourceOptionNumber,
                                                              SourceOptionStatus = o.SourceOptionStatus,
                                                              IsActive = o.IsActive,
                                                              EntryBy = o.EntryBy,
                                                              EntryDtTime = o.EntryDtTime,
                                                              EntryDtTimeUtc = o.EntryDtTimeUtc,
                                                              SourceLinkStatus = o.SourceLinkStatus,
                                                              ReceivedId = oLink.ReceivedId,
                                                              CorpId = oLink.CorpId,
                                                              MessageType = oLink.MessageType,
                                                              SemeRef = oLink.SemeRef,
                                                              SourceEventMappingId = oLink.GoldenRecordSourceEventMappingId
                                                          }).ToList();

                var optionLinkDbAudit = (from o in this.context.CaOptionLinks
                                         join a in this.context.GoldenRecordSourceEventMappingAudits on o.SourceEventId equals a.SemeRef into grOptionLink
                                         from oLink in grOptionLink.DefaultIfEmpty()
                                         where (o.GoldenRecordId == goldenId) && (o.IsActive == 1)
                                         select new OptionLinkDbResult
                                         {
                                             CaOptionLinkId = o.CaOptionLinkId,
                                             GoldenRecordId = o.GoldenRecordId,
                                             ClientLinkStatus = o.ClientLinkStatus,
                                             ClientOptionNumber = o.ClientOptionNumber,
                                             ClientOptionStatus = o.ClientOptionStatus,
                                             SourceName = o.SourceName,
                                             SourceBic = o.SourceBic,
                                             SourceEventId = o.SourceEventId,
                                             SourceOptionNumber = o.SourceOptionNumber,
                                             SourceOptionStatus = o.SourceOptionStatus,
                                             IsActive = o.IsActive,
                                             EntryBy = o.EntryBy,
                                             EntryDtTime = o.EntryDtTime,
                                             EntryDtTimeUtc = o.EntryDtTimeUtc,
                                             SourceLinkStatus = o.SourceLinkStatus,
                                             ReceivedId = oLink.ReceivedId,
                                             CorpId = oLink.CorpId,
                                             MessageType = oLink.MessageType,
                                             SemeRef = oLink.SemeRef,
                                             SourceEventMappingId = oLink.GoldenRecordSourceEventMappingId
                                         }).ToList();

                optionLinksDb.AddRange(optionLinkDbAudit.Where(x => x.ReceivedId != null));

                optionLinksDb = optionLinksDb.GroupBy(x => new { x.ClientOptionNumber, x.SourceOptionNumber, x.SourceName }, (key, g) => g.OrderBy(e => e.SourceEventMappingId).First()).ToList();

                //List<CaOptionLink> optionLinksDb = this.context.CaOptionLinks.Where(a => a.GoldenRecordId == goldenId).ToList();
                if (optionLinksDb.Count > 0)
                {
                    //MT564Message mt564 = this.swiftContext.GetSwiftMessageByIdWithCriteria(a => a.GoldenRecordID == goldenId, false);
                    CaapsLinqToDB.DBModels.MT564Message mt564New = this.linqToDbHelper.GetEventDetailsByGolderRecordId(goldenId);
                    List<CAOptionsLink> optionsLinks = new List<CAOptionsLink>();
                    foreach (var optionLink in optionLinksDb)
                    {
                        try
                        {
                            CAOptionsLink caOptions = new CAOptionsLink();
                            caOptions.CaOptionLinkId = optionLink.CaOptionLinkId;
                            caOptions.GoldenRecordId = optionLink.GoldenRecordId;
                            caOptions.ClientLinkStatus = optionLink.ClientLinkStatus;
                            caOptions.ClientOptionNumber = optionLink.ClientOptionNumber;
                            caOptions.ClientOptionStatus = optionLink.ClientOptionStatus;
                            caOptions.SourceName = optionLink.SourceName;
                            caOptions.SourceBic = optionLink.SourceBic;
                            caOptions.SourceEventId = optionLink.SourceEventId;
                            caOptions.SourceOptionNumber = optionLink.SourceOptionNumber;
                            caOptions.SourceOptionStatus = optionLink.SourceOptionStatus;
                            caOptions.IsActive = optionLink.IsActive;
                            caOptions.EntryBy = optionLink.EntryBy;
                            caOptions.EntryDtTime = optionLink.EntryDtTime;
                            caOptions.EntryDtTimeUtc = optionLink.EntryDtTimeUtc;
                            caOptions.SourceLinkStatus = optionLink.SourceLinkStatus;
                            if (mt564New.MessageData.CorporateActionOptions.Count > 0 && optionLink.ClientOptionNumber != String.Empty)
                            {
                                CaapsLinqToDB.DBModels.MCorporateActionOption mCorporateActionOption = mt564New.MessageData.CorporateActionOptions.ToList().Find(a => a.CaoptionNumber.NumberId == optionLink.ClientOptionNumber);
                                if (mCorporateActionOption != null)
                                {
                                    CaapsLinqToDB.DBModels.MIndicator indc = mCorporateActionOption.Indicator.Where(x => x.Qualifier == "CAOP").FirstOrDefault();
                                    CaapsLinqToDB.DBModels.MFlag mFlag = mCorporateActionOption.Flag.FirstOrDefault();
                                    caOptions.ClientOptionType = indc.Indicator;
                                    if (mFlag.Qualifier == "DFLT")
                                    {
                                        caOptions.ClientDefault = mFlag.Flag;
                                    }
                                }
                            }

                            if (optionLink.ReceivedId != String.Empty && optionLink.ReceivedId != null)
                            {
                                ReceivedCaEventDtl receivedCaEventDtl = this.context.ReceivedCaEventDtls.Where(a => a.ReceivedCaEventDtlId == int.Parse(optionLink.ReceivedId)).FirstOrDefault();
                                if (receivedCaEventDtl != null && receivedCaEventDtl.NormalizedMessage != null)
                                {
                                    CaapsLinqToDB.DBModels.MT564Message sourceMT564 = JsonConvert.DeserializeObject<CaapsLinqToDB.DBModels.MT564Message>(receivedCaEventDtl.NormalizedMessage);

                                    if (sourceMT564 != null && sourceMT564.MessageData.CorporateActionOptions.Count > 0)
                                    {
                                        CaapsLinqToDB.DBModels.MCorporateActionOption mCorporateActionOption = sourceMT564.MessageData.CorporateActionOptions.ToList().Find(a => a.CaoptionNumber.NumberId == optionLink.SourceOptionNumber);
                                        if (mCorporateActionOption != null)
                                        {
                                            CaapsLinqToDB.DBModels.MIndicator indc = mCorporateActionOption.Indicator.Where(x => x.Qualifier == "CAOP").FirstOrDefault();
                                            CaapsLinqToDB.DBModels.MFlag mFlag = mCorporateActionOption.Flag.FirstOrDefault();
                                            caOptions.SourceOptionType = indc.Indicator;
                                            if (mFlag.Qualifier == "DFLT")
                                            {
                                                caOptions.SourceDefault = mFlag.Flag;
                                            }
                                        }
                                    }
                                }
                            }
                            optionsLinks.Add(caOptions);
                        }
                        catch (Exception ex)
                        {
                            dynamic logs = new ExpandoObject();
                            logs.StackTrace = ex.StackTrace;
                            logs.goldenRecordId = goldenId;
                            Logger.LogException(ex, LogType.Error, logs);
                        }
                    }
                    List<CAOptionsLink> sourceOptionsLinks = optionsLinks.Where(a => a.SourceName != null && a.SourceName != String.Empty).ToList();
                    Dictionary<string, List<CAOptionsLink>> sourceOptions = sourceOptionsLinks.OrderByDescending(a => a.EntryDtTimeUtc).GroupBy(a => a.SourceName).ToDictionary(a => a.Key, a => a.DistinctBy(x => x.SourceOptionNumber).ToList());
                    optionsLinkResponse.SourceOptions = sourceOptions;
                    optionsLinkResponse.LinkedOptions = optionsLinks.Where(a => a.ClientOptionNumber != "" && a.SourceOptionNumber != "").ToList();
                    optionsLinkResponse.ClientOptions = optionsLinks.Where(a => a.ClientOptionNumber != "" && a.SourceOptionNumber != "").OrderBy(a => a.ClientOptionNumber).ThenBy(a => a.ClientLinkStatus).DistinctBy(a => a.ClientOptionNumber).ToList();
                    if (optionsLinkResponse.ClientOptions.Count != optionsLinks.Where(a => a.ClientOptionNumber != "").DistinctBy(a => a.ClientOptionNumber).ToList().Count)
                    {
                        var tempOptionLinks = optionsLinks;
                        foreach (var tempOptionLink in tempOptionLinks)
                        {
                            if (!optionsLinkResponse.ClientOptions.Any(x => x.ClientOptionNumber == tempOptionLink.ClientOptionNumber))
                            {
                                optionsLinkResponse.ClientOptions.Add(tempOptionLink);
                            }
                        }
                    }
                    optionsLinkResponse.ClientOptions = optionsLinkResponse.ClientOptions.OrderBy(x => x.ClientOptionNumber).ToList();
                }
            }
            catch (Exception ex)
            {
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.goldenRecordId = goldenId;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }
            return optionsLinkResponse;
        }
        public OptionsLinkRequest SaveLinkOptions(OptionsLinkRequest optionsLink, int UserId)
        {
            OptionsLinkResponse optionsLinkResponse = new OptionsLinkResponse();
            try
            {
                List<CaOptionLink> optionLinksDb = this.context.CaOptionLinks.Where(a => a.GoldenRecordId == optionsLink.GoldenRecordId).ToList();
                if (optionLinksDb.Count > 0)
                {
                    foreach (var option in optionsLink.LinkOptions)
                    {
                        CaOptionLink caOptionLink = optionLinksDb.Find(a => a.ClientOptionNumber == option.ClientOptionNumber && a.SourceOptionNumber == option.SourceOptionNumber && a.SourceName == option.SourceName);
                        if (caOptionLink != null)
                        {
                            caOptionLink.SourceLinkStatus = option.SourceLinkStatus;
                            caOptionLink.ClientLinkStatus = option.ClientLinkStatus;
                            caOptionLink.EntryBy = UserId;
                            caOptionLink.EntryDtTime = DateTime.Now;
                            caOptionLink.EntryDtTimeUtc = DateTime.UtcNow;

                            CaOptionException sourceException = this.context.CaOptionExceptions.Where(x => x.GoldenRecordId == optionsLink.GoldenRecordId
                            && x.ExceptionType == "Unlinked Source Option" && x.SourceName == caOptionLink.SourceName && x.OptionNumber == caOptionLink.SourceOptionNumber
                            ).FirstOrDefault();

                            CaOptionException clientException = this.context.CaOptionExceptions.Where(x => x.GoldenRecordId == optionsLink.GoldenRecordId
                            && x.ExceptionType == "Unlinked Client Option" && x.SourceName == caOptionLink.SourceName && x.OptionNumber == caOptionLink.ClientOptionNumber
                            ).FirstOrDefault();

                            if (sourceException != null)
                            {
                                if (caOptionLink.SourceLinkStatus == "Unlinked")
                                {
                                    sourceException.ExceptionStatus = "Open";
                                }
                                else if (caOptionLink.SourceLinkStatus == "Linked")
                                {
                                    sourceException.ExceptionStatus = "User Resolved";
                                }

                                this.context.CaOptionExceptions.Update(sourceException);
                            }
                            else
                            {
                                sourceException = new CaOptionException();
                                if (caOptionLink.SourceLinkStatus == "Unlinked")
                                {
                                    sourceException.GoldenRecordId = optionsLink.GoldenRecordId;
                                    sourceException.ExceptionType = "Unlinked Source Option";
                                    sourceException.ExceptionMessage = "Unlinked manually by User";
                                    sourceException.SourceName = option.SourceName;
                                    sourceException.SourceBic = option.SourceBic;
                                    sourceException.SourceEventId = option.SourceEventId;
                                    sourceException.OptionNumber = option.SourceOptionNumber;
                                    sourceException.IsActive = 1;
                                    sourceException.EntryBy = UserId;
                                    sourceException.EntryDtTime = DateTime.Now;
                                    sourceException.EntryDtTimeUtc = DateTime.UtcNow;
                                    sourceException.MovementId = "";
                                    if (caOptionLink.SourceLinkStatus == "Unlinked")
                                    {
                                        sourceException.ExceptionStatus = "Open";
                                    }
                                    else if (caOptionLink.SourceLinkStatus == "Linked")
                                    {
                                        sourceException.ExceptionStatus = "User Resolved";
                                    }

                                    this.context.CaOptionExceptions.Add(sourceException);
                                }

                            }

                            if (clientException != null)
                            {
                                if (caOptionLink.ClientLinkStatus == "Unlinked")
                                {
                                    clientException.ExceptionStatus = "Open";
                                }
                                else if (caOptionLink.ClientLinkStatus == "Linked")
                                {
                                    clientException.ExceptionStatus = "User Resolved";
                                }

                                this.context.CaOptionExceptions.Update(clientException);
                            }
                            else
                            {
                                clientException = new CaOptionException();
                                if (caOptionLink.ClientLinkStatus == "Unlinked")
                                {
                                    clientException.GoldenRecordId = optionsLink.GoldenRecordId;
                                    clientException.ExceptionType = "Unlinked Client Option";
                                    clientException.ExceptionMessage = "Unlinked manually by User";
                                    clientException.SourceName = option.SourceName;
                                    clientException.SourceBic = option.SourceBic;
                                    clientException.SourceEventId = option.SourceEventId;
                                    clientException.OptionNumber = option.ClientOptionNumber;
                                    clientException.IsActive = 1;
                                    clientException.EntryBy = UserId;
                                    clientException.EntryDtTime = DateTime.Now;
                                    clientException.EntryDtTimeUtc = DateTime.UtcNow;
                                    clientException.MovementId = "";
                                    if (caOptionLink.ClientLinkStatus == "Unlinked")
                                    {
                                        clientException.ExceptionStatus = "Open";
                                    }
                                    else if (caOptionLink.ClientLinkStatus == "Linked")
                                    {
                                        clientException.ExceptionStatus = "User Resolved";
                                    }

                                    this.context.CaOptionExceptions.Add(clientException);
                                }
                            }
                        }
                        else
                        {
                            CaOptionLink newOptionLink = new CaOptionLink();
                            newOptionLink.GoldenRecordId = optionsLink.GoldenRecordId;
                            newOptionLink.ClientLinkStatus = "Linked";
                            newOptionLink.ClientOptionNumber = option.ClientOptionNumber;
                            newOptionLink.SourceName = option.SourceName;
                            newOptionLink.SourceBic = option.SourceBic;
                            newOptionLink.SourceEventId = option.SourceEventId;
                            newOptionLink.SourceOptionNumber = option.SourceOptionNumber;
                            newOptionLink.IsActive = 1;
                            newOptionLink.EntryBy = UserId;
                            newOptionLink.SourceLinkStatus = option.SourceLinkStatus;
                            newOptionLink.EntryDtTime = DateTime.Now;
                            newOptionLink.EntryDtTimeUtc = DateTime.UtcNow;
                            newOptionLink.ClientOptionStatus = "";
                            newOptionLink.SourceOptionStatus = "";
                            this.context.CaOptionLinks.Add(newOptionLink);

                            CaOptionException sourceException = this.context.CaOptionExceptions.Where(x => x.GoldenRecordId == optionsLink.GoldenRecordId
                            && x.ExceptionType == "Unlinked Source Option" && x.SourceName == newOptionLink.SourceName && x.OptionNumber == newOptionLink.SourceOptionNumber
                            ).FirstOrDefault();

                            CaOptionException clientException = this.context.CaOptionExceptions.Where(x => x.GoldenRecordId == optionsLink.GoldenRecordId
                            && x.ExceptionType == "Unlinked Client Option" && x.SourceName == newOptionLink.SourceName && x.OptionNumber == newOptionLink.ClientOptionNumber
                            ).FirstOrDefault();

                            if (sourceException != null)
                            {
                                sourceException.ExceptionStatus = "User Resolved";
                                this.context.CaOptionExceptions.Update(sourceException);
                            }

                            if (clientException != null)
                            {
                                clientException.ExceptionStatus = "User Resolved";
                                this.context.CaOptionExceptions.Update(clientException);
                            }

                            //caOptionLink = optionLinksDb.Find(a => a.ClientOptionNumber == option.ClientOptionNumber && a.SourceName == option.SourceName && a.SourceOptionNumber == "");
                            //if (caOptionLink != null)
                            //{
                            //    caOptionLink.SourceOptionNumber = caOptionLink.SourceOptionNumber;
                            //    caOptionLink.SourceLinkStatus = option.SourceLinkStatus;
                            //    caOptionLink.ClientLinkStatus = option.ClientLinkStatus;
                            //    caOptionLink.EntryBy = UserId;
                            //    caOptionLink.EntryDtTime = DateTime.Now;
                            //    caOptionLink.EntryDtTimeUtc = DateTime.UtcNow;
                            //}
                            //else
                            //{
                            //    caOptionLink = optionLinksDb.Find(a => a.SourceOptionNumber == option.SourceOptionNumber && a.SourceName == option.SourceName);
                            //    caOptionLink.ClientOptionNumber = caOptionLink.SourceOptionNumber;
                            //    caOptionLink.SourceLinkStatus = option.SourceLinkStatus;
                            //    caOptionLink.ClientLinkStatus = option.ClientLinkStatus;
                            //    caOptionLink.EntryBy = UserId;
                            //    caOptionLink.EntryDtTime = DateTime.Now;
                            //    caOptionLink.EntryDtTimeUtc = DateTime.UtcNow;
                            //}
                        }
                        this.context.SaveChanges();
                    }
                }
                optionsLink.IsSuccess = true;
                optionsLink.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                optionsLink.MessageId = (int)ResponseCodeMessage.Successful;
            }
            catch (Exception ex)
            {
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.optionLink = JsonConvert.SerializeObject(optionsLink);
                Logger.LogException(ex, LogType.Error, logs);
                optionsLink.IsSuccess = false;
                optionsLink.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InternalServerError);
                optionsLink.MessageId = (int)ResponseCodeMessage.InternalServerError;
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            sendEventNotification(
                     $"Option Linking Performed",
                     "LINK_OPTION",
                      new
                      {
                          GoldenId = optionsLink.GoldenRecordId
                      });

            //sendEventNotification(
            //        $"Event approval perform",
            //        "EVENT_APPROVAL",
            //        new
            //        {
            //            GoldenId = optionsLink.GoldenRecordId
            //        });

            return optionsLink;
        }

        public void SaveCaOptions()
        {
            try
            {
                MT564Message mt564 = this.swiftContext.GetSwiftMessageByIdWithCriteria(a => a.GoldenRecordID == 10329, true);
                DisplayData displayData = this.displayDataHelper.GetDisplayData(a => a.GoldenRecordID == 10329, false);
                DisplayToMt564 displayToMt564 = new DisplayToMt564();
                mt564 = displayToMt564.TransformDisplayToMt564(mt564, displayData);
                this.swiftContext.SaveChanges();
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }
        }
        public List<CaOptionException> GetEventExceptions(int goldenRecordId)
        {
            List<CaOptionException> eventExceptions = new List<CaOptionException>();
            try
            {
                eventExceptions = this.context.CaOptionExceptions.Where(x => x.GoldenRecordId == goldenRecordId).ToList();
            }
            catch (Exception ex)
            {
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.goldenRecordId = goldenRecordId;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            return eventExceptions;
        }

        public List<CAOptions> GetSourceOptionsByGoldenId(int goldenId, string source)
        {
            List<CAOptions> cAOptions = new List<CAOptions>();
            try
            {
                GoldenRecordSourceEventMapping goldenRecordSourceEventMapping =
                    this.context.GoldenRecordSourceEventMappings.Where(a => a.GoldenRecordId == goldenId && a.SourceName.ToUpper() == source.ToUpper()).OrderByDescending(x => x.EntryDtTimeUtc).FirstOrDefault();
                ReceivedCaEventDtl receivedCaEventDtl = this.context.ReceivedCaEventDtls.Where(a => a.ReceivedCaEventDtlId == int.Parse(goldenRecordSourceEventMapping.ReceivedId)).FirstOrDefault();
                if ((receivedCaEventDtl?.NormalizedMessage ?? null) != null)
                {
                    CaapsLinqToDB.DBModels.MT564Message sourceMT564 = JsonConvert.DeserializeObject<CaapsLinqToDB.DBModels.MT564Message>(receivedCaEventDtl.NormalizedMessage);
                    DisplayData displayData = this.displayDataHelper.ToDisplayData(sourceMT564);
                    cAOptions = displayData.Options;
                    List<CaOptionLink> optionLinksDb = this.context.CaOptionLinks.Where(a => a.GoldenRecordId == goldenId).ToList();
                    cAOptions.ForEach(option =>
                    {
                        try
                        {
                            if (optionLinksDb != null)
                            {
                                var linkDetail = optionLinksDb.Where(x => x.SourceOptionNumber == option.CaOptOptionNumber && x.SourceName.ToUpper() == source.ToUpper() && !String.IsNullOrEmpty(x.SourceLinkStatus));
                                if (linkDetail != null)
                                {
                                    option.LinkStatus = linkDetail.Any(x => x.ClientLinkStatus == "Linked") ? "Linked" : "Unlinked";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            dynamic logs = new ExpandoObject();
                            logs.StackTrace = ex.StackTrace;
                            logs.goldenRecordId = goldenId;
                            logs.source = source;
                            Logger.LogException(ex, LogType.Error, logs);
                        }

                    });

                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogType.Error, ex.StackTrace);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, ex.StackTrace);
            }
            return cAOptions;
        }

        public AllSourceResponse GetAllSourceByGoldenId(long goldenId)
        {
            AllSourceResponse allSourceResponse = new AllSourceResponse();
            try
            {
                List<CaOptionLink> optionLinksDb = this.context.CaOptionLinks.Where(a => a.GoldenRecordId == goldenId).DistinctBy(a => a.SourceName).ToList();
                allSourceResponse.Sources = new List<EventSource>();
                foreach (var source in optionLinksDb)
                {
                    if (source.SourceOptionNumber != null && source.SourceOptionNumber != String.Empty)
                    {
                        EventSource eventSource = new EventSource();
                        eventSource.EventSourceId = source.CaOptionLinkId;
                        eventSource.EventSourceName = source.SourceName;
                        allSourceResponse.Sources.Add(eventSource);
                    }
                }
                if (allSourceResponse.Sources.Count == 0)
                {
                    List<GoldenRecordSourceEventMapping> sourcesData = this.context.GoldenRecordSourceEventMappings.Where(x => x.GoldenRecordId == goldenId).DistinctBy(a => a.SourceName).ToList();
                    foreach (var source in sourcesData)
                    {
                        EventSource eventSource = new EventSource();
                        eventSource.EventSourceId = source.GoldenRecordSourceEventMappingId;
                        eventSource.EventSourceName = source.SourceName;
                        allSourceResponse.Sources.Add(eventSource);
                    }
                }
                allSourceResponse.IsSuccess = true;
                allSourceResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                allSourceResponse.MessageId = (int)ResponseCodeMessage.Successful;
            }
            catch (Exception ex)
            {
                allSourceResponse.IsSuccess = false;
                allSourceResponse.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InternalServerError);
                allSourceResponse.MessageId = (int)ResponseCodeMessage.InternalServerError;
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.goldenRecordId = goldenId;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }
            return allSourceResponse;
        }

        public ResponseParent UpdateMT565StatusPosition(int goldenRecordId, int CaEventElectionOptionsDtlId, string Status, string Role, string ExternalComment, int userId)
        {
            ResponseParent res = new ResponseParent();
            try
            {
                if (Role != UserRoleConsts.SUPERVISOR && Role != UserRoleConsts.SYSTEM_ADMIN)
                {
                    res.IsSuccess = false;
                    res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.NoPermission);
                    res.MessageId = (int)ResponseCodeMessage.NoPermission;
                }
                else
                {
                    CaEventElectionOptionsDtl eventDtl = this.context.CaEventElectionOptionsDtls.Where(c => c.GoldenRecordId == goldenRecordId &&
                                                                                                             c.CaEventElectionOptionsDtlId == CaEventElectionOptionsDtlId).FirstOrDefault();

                    var isDefault = this.context.CaEventElectionMappings.Where(x => x.CaEventElectionMappingId == eventDtl.CaEventElectionMappingId)
                        .Select(x => new
                        {
                            x.IsDefault,
                            x.Mt564SemeRef
                        }).FirstOrDefault();

                    if (eventDtl != null)
                    {
                        eventDtl.OptionStatus = Status;
                        eventDtl.ExternalComment = ExternalComment;
                        eventDtl.EntryBy = userId;
                        eventDtl.EntryDtTime = DateTime.Now;
                        eventDtl.EntryDtTimeUtc = DateTime.UtcNow;
                        this.context.SaveChanges();
                        MT565MsgFromSwiftProcessor obj = new MT565MsgFromSwiftProcessor();
                        if (isDefault.IsDefault == 1)
                        {
                            obj.CorpRef = Convert.ToString(goldenRecordId);
                            obj.mt565Ref = eventDtl.CaEventElectionOptionsDtlId.ToString();
                            obj.mt564Ref = isDefault.Mt564SemeRef;
                            obj.type = eventDtl.OptionStatus;
                            obj.mt565Json = null;
                            obj.isDefault = true;
                        }
                        else
                        {
                            obj.CorpRef = Convert.ToString(goldenRecordId);
                            obj.mt565Ref = eventDtl.Mt565SemeRef;
                            obj.mt564Ref = "";
                            obj.type = eventDtl.OptionStatus;
                            obj.mt565Json = null;
                            obj.isDefault = false;
                        }


                        if (solaceSettings.SendToTopic)
                        {
                            this.solaceMessageListener.SendToTopic(solaceSettings.MT565OutBoundTopicName, JsonConvert.SerializeObject(obj));
                        }
                        else
                        {
                            this.solaceMessageListener.SendToQueue(solaceSettings.MT565OutBoundQueueName, JsonConvert.SerializeObject(obj));
                        }
                        //this.solaceMessageListener.SendMessage(solaceSettings.MT565OutBoundQueueName, JsonConvert.SerializeObject(obj));
                        //this.solaceMessageListener.SendToQueue("caaps_dev_outboundqueue_mt567", JsonConvert.SerializeObject(obj));

                        res.IsSuccess = true;
                        res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.Successful);
                        res.MessageId = (int)ResponseCodeMessage.Successful;
                    }
                }

            }
            catch (Exception ex)
            {
                res.IsSuccess = false;
                res.Message = this.enumHelper.GetEnumDescription(ResponseCodeMessage.InternalServerError);
                res.MessageId = (int)ResponseCodeMessage.InternalServerError;
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.goldenRecordId = goldenRecordId;
                logs.CaEventElectionOptionsDtlId = CaEventElectionOptionsDtlId;
                logs.Status = Status;
                logs.Role = Role;
                logs.UserId = userId;
                Logger.LogException(ex, LogType.Error, logs);
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            return res;
        }

        public ResponseParent SendSolaceMessage(string queueName, MT564Message updatedMessage, GoldenRecordMst golderRecordDetails)
        {
            ResponseParent res = new ResponseParent();

            try
            {
                SolacePositionCheckModel spCM = new SolacePositionCheckModel();
                spCM.Type = "event";
                spCM.GoldenRecordId = golderRecordDetails.GoldenRecordId;
                spCM.EventMsg = JsonConvert.SerializeObject(updatedMessage);
                spCM.PositionMsg = null;
                spCM.BBGID = golderRecordDetails.BloombergId;
                spCM.IsAutoStp = (bool)golderRecordDetails.IsAutoStp;
                spCM.InternalID = (long)golderRecordDetails.InternalSecurityId;
                spCM.EventStatus = golderRecordDetails.GoldenRecordStatus;

                if (solaceSettings.SendToTopic)
                {
                    this.solaceMessageListener.SendToTopic(solaceSettings.PositionCheckTopicName, JsonConvert.SerializeObject(spCM));
                }
                else
                {
                    this.solaceMessageListener.SendToQueue(solaceSettings.PositionCheckQueueName, JsonConvert.SerializeObject(spCM));
                }


                res.IsSuccess = true;
                res.Message = "Solace Message send Success";
                res.MessageId = 200;
            }
            catch (Exception ex)
            {
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.QueueName = queueName;
                logs.Message = JsonConvert.SerializeObject(updatedMessage);
                logs.GoldenRecordDetails = JsonConvert.SerializeObject(golderRecordDetails);
                Logger.LogException(ex, LogType.Error, logs);
                res.IsSuccess = false;
                res.Message = "Solace Message send Failed";
                res.MessageId = 400;
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            return res;
        }

        public ResponseParent SendSolaceMessageLinqDB(string queueName, CaapsLinqToDB.DBModels.MT564Message updatedMessage, GoldenRecordMst golderRecordDetails)
        {
            ResponseParent res = new ResponseParent();

            try
            {
                var msgString = JsonConvert.SerializeObject(updatedMessage, Formatting.None, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
                SolacePositionCheckModel spCM = new SolacePositionCheckModel();
                spCM.Type = "event";
                spCM.GoldenRecordId = golderRecordDetails.GoldenRecordId;
                spCM.EventMsg = msgString;
                spCM.PositionMsg = null;
                spCM.BBGID = golderRecordDetails.BloombergId;
                spCM.IsAutoStp = (bool)golderRecordDetails.IsAutoStp;
                spCM.InternalID = (long)golderRecordDetails.InternalSecurityId;
                spCM.EventStatus = golderRecordDetails.GoldenRecordStatus;

                if (solaceSettings.SendToTopic)
                {
                    this.solaceMessageListener.SendToTopic(solaceSettings.PositionCheckTopicName, JsonConvert.SerializeObject(spCM));
                    this.solaceMessageListener.SendToTopic(solaceSettings.GoldenRecordAPITopicName, JsonConvert.SerializeObject(spCM));
                }
                else
                {
                    this.solaceMessageListener.SendToQueue(solaceSettings.PositionCheckQueueName, JsonConvert.SerializeObject(spCM));
                    this.solaceMessageListener.SendToQueue(solaceSettings.GoldenRecordAPIQueueName, JsonConvert.SerializeObject(spCM));
                }

                res.IsSuccess = true;
                res.Message = "Solace Message send Success";
                res.MessageId = 200;
            }
            catch (Exception ex)
            {
                dynamic logs = new ExpandoObject();
                logs.StackTrace = ex.StackTrace;
                logs.QueueName = queueName;
                logs.Message = JsonConvert.SerializeObject(updatedMessage, Formatting.None, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
                logs.GoldenRecordDetails = JsonConvert.SerializeObject(golderRecordDetails);
                Logger.LogException(ex, LogType.Error, logs);
                res.IsSuccess = false;
                res.Message = "Solace Message send Failed";
                res.MessageId = 400;
                this.appNotificationSender.sendDbFailureNotification(ex.Message, logs);
            }

            return res;
        }

       
      
    }
}

