//
//  MainView.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/28/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import CoreNFC
import MobileCoreServices

class MainViewController: UIViewController, NFCTagReaderSessionDelegate, UIDocumentPickerDelegate, ScannerViewControllerDelegate {
    func scannerCodeFound(code: String) {
        guard let data = try? Data(base64Encoded: code, options: .ignoreUnknownCharacters) else {
            return
        }
        
        if (data.count == 532 || data.count == 540 || data.count == 572) {
            if let dump = TagDump(data: data) {
                dismiss(animated: true) {
                    self.openTagInfo(dump: dump)
                }
            }
        }
    }
    
    var tagReaderSession: NFCTagReaderSession?
    var pickerController: UIDocumentPickerViewController!
    
    @IBAction func loadTagTap(_ sender: Any) {
        self.present(pickerController, animated: true)
    }
    
    @IBAction func startTagReadingSession() {
        tagReaderSession = NFCTagReaderSession(pollingOption: NFCTagReaderSession.PollingOption.iso14443, delegate: self)
        tagReaderSession?.alertMessage = "Hold tag to back of phone!"
        tagReaderSession?.begin()
    }
    
    @IBAction func scanQrTap(_ sender: Any) {
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let vc = storyboard.instantiateViewController(withIdentifier: "ScannerView") as? ScannerViewController else {
            return
        }
        vc.delegate = self
        self.present(vc, animated: true)
    }
    
    func openTagInfo(dump: TagDump){
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let vc = storyboard.instantiateViewController(withIdentifier: "TagInfo") as? TagInfoViewController else {
            return
        }
        self.present(vc, animated: true)
        vc.amiiboData = dump
    }
    
    func handleConnectedTag(tag: NFCMiFareTag) {
        NTAG215Tag.initialize(tag: tag) { result in
            switch result {
            case .success(let ntag215Tag):
                self.tagReaderSession?.invalidate()
                DispatchQueue.main.async {
                    print(ntag215Tag.dump.TagUIDSig)
                    self.writeUidSig(data: ntag215Tag.dump.TagUIDSig!)
                    self.openTagInfo(dump: ntag215Tag.dump)
                }
            case .failure(let error):
                self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
            }
        }
    }
    
    func writeUidSig(data: Data) {
        let fileManager = FileManager.default
        let documentsURL =  fileManager.urls(for: .documentDirectory, in: .userDomainMask).first!

        let sigPath = documentsURL.appendingPathComponent("signatures")
        do
        {
            try FileManager.default.createDirectory(atPath: sigPath.path, withIntermediateDirectories: true, attributes: nil)
            try data.write(to: sigPath.appendingPathComponent("\(data[0..<9].map { String(format: "%02hhx", $0) }.joined()).bin"))
        }
        catch let error as NSError
        {
            NSLog("Unable to create directory \(error.debugDescription)")
        }
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        
        print("Loaded \(NTAG215Tag.uidSignatures.count) UID/Signature pairs")
        print("Loaded \(AmiiboDatabase.amiiboDumps.count) amiibo dumps")
        let dumps = AmiiboDatabase.amiiboDumps
        for (key, dump) in AmiiboDatabase.database.AmiiboData {
            if dumps["\(key.suffix(16))"] == nil {
                print("\(key) - \(dump)")
            }
        }
        
        pickerController = UIDocumentPickerViewController(documentTypes: [kUTTypeData as String], in: .open)
        pickerController.delegate = self
        pickerController.allowsMultipleSelection = false
    }
    
    // MARK: DocumentDelegate
    public func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else {
            return
        }
        if url.isFileURL {
            let shouldStopAccessing = url.startAccessingSecurityScopedResource()
            let path = url.path
            if let attr = try? FileManager.default.attributesOfItem(atPath: path) as NSDictionary {
                if attr.fileSize() == 532 || attr.fileSize() == 540 || attr.fileSize() == 572 {
                    if let data = try? Data(contentsOf: url),
                        let dump = TagDump(data: data) {
                        self.openTagInfo(dump: dump)
                    }
                    
                }
            }
            if shouldStopAccessing {
                url.stopAccessingSecurityScopedResource()
            }
        }
    }
    
    // MARK: NFCTagReaderSessionDelegate
    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {
        NSLog("tagReaderSessionDidBecomeActive")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        NSLog("NFCTagReaderSession, didInvalidateWithError \(error)")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        guard case let NFCTag.miFare(tag) = tags.first! else {
            tagReaderSession?.invalidate(errorMessage: "Invalid tag type.")
            return
        }
        session.connect(to: tags.first!) { (error: Error?) in
            if let error = error {
                session.invalidate(errorMessage: error.localizedDescription)
            } else {
                self.handleConnectedTag(tag: tag)
            }
        }
    }
}
